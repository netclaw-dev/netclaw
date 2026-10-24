// -----------------------------------------------------------------------
// <copyright file="DaemonClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Akka.Configuration;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using R3;
using AkkaConfig = Akka.Configuration.Config;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Cli.Daemon.ChatClientProtocol;

namespace Netclaw.Cli.Daemon;

/// <summary>Local actor facade for daemon sessions.</summary>
public sealed class DaemonClient : IAsyncDisposable
{
    public static readonly ChannelType TuiChannelType = ChannelType.Tui;
    internal static readonly TimeSpan[] DefaultReconnectDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    internal static readonly TimeSpan DefaultRpcTimeout = TimeSpan.FromSeconds(60);

    private readonly IDaemonHubTransport _transport;
    private readonly Subject<SessionOutput> _outputSubject = new();
    private readonly Subject<DaemonConnectionEvent> _connectionSubject = new();
    private readonly Channel<ClientEvent> _events = Channel.CreateUnbounded<ClientEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lazy<Task<Runtime>> _runtime;
    private Queue<Request> _pending = new();
    private readonly TimeProvider _clock;
    private Close? _startupClose;
    private ITimer? _startupCloseTimer;
    private readonly Lazy<Task<ChatCloseReceipt>> _close;
    private readonly IDisposable _transportSubscription;
    private enum Lifetime { Open, Closing, Disposed }
    private Lifetime _lifetime;

    public DaemonClient(string daemonEndpoint, TimeProvider? timeProvider = null,
        TimeSpan[]? reconnectDelays = null, TimeSpan? serverTimeout = null,
        Func<Task<string?>>? accessTokenProvider = null)
        : this(daemonEndpoint, SignalRDaemonHubTransport.Create(
            $"{NormalizeEndpoint(daemonEndpoint)}/hub/session", accessTokenProvider, serverTimeout),
            timeProvider, reconnectDelays, null)
    { }

    internal DaemonClient(string daemonEndpoint, IDaemonHubTransport transport,
        TimeProvider? timeProvider = null, TimeSpan[]? reconnectDelays = null, TimeSpan? rpcTimeout = null)
        : this(daemonEndpoint, transport, ActorSystem.Create, timeProvider, reconnectDelays, rpcTimeout)
    { }

    internal DaemonClient(string daemonEndpoint, IDaemonHubTransport transport,
        Func<string, AkkaConfig, ActorSystem> actorSystemFactory, TimeProvider? timeProvider,
        TimeSpan[]? reconnectDelays, TimeSpan? rpcTimeout)
    {
        ArgumentNullException.ThrowIfNull(actorSystemFactory);
        var endpoint = NormalizeEndpoint(daemonEndpoint);
        var delays = (reconnectDelays ?? DefaultReconnectDelays).ToArray();
        if (delays.Length == 0) throw new ArgumentException("At least one reconnect delay is required.", nameof(reconnectDelays));
        _transport = transport;
        _clock = timeProvider ?? TimeProvider.System;
        _transportSubscription = transport.Subscribe(OnTransportEvent);
        _runtime = new Lazy<Task<Runtime>>(() => Task.Run(() =>
        {
            var system = actorSystemFactory($"chat-client-{Guid.NewGuid():N}", ConfigurationFactory.ParseString("""
                akka.actor.provider = local
                akka.actor.guardian-supervisor-strategy = "Akka.Actor.StoppingSupervisorStrategy, Akka"
                akka.loglevel = ERROR
                """));
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var actor = system.ActorOf(Props.Create(() => new ChatClientActor(
                transport, endpoint, delays, rpcTimeout ?? DefaultRpcTimeout,
                _clock, _events.Writer, stopped)), "client");
            return new Runtime(system, actor, stopped.Task, Task.Run(EventPumpAsync));
        }));
        _close = new Lazy<Task<ChatCloseReceipt>>(() =>
        {
            lock (_runtime)
            {
                ObjectDisposedException.ThrowIf(_lifetime == Lifetime.Disposed, this);
                _lifetime = Lifetime.Closing;
                if (!_runtime.IsValueCreated) return Task.FromResult(new ChatCloseReceipt(null, []));
                var reply = new TaskCompletionSource<ChatCloseReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
                var close = _startupClose = new Close(reply, _clock.GetTimestamp())
                {
                    Requests = _pending
                };
                _startupCloseTimer = _clock.CreateTimer(_ => ExpireStartup(close), null,
                    TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                var runtime = PostAfterAsync(_runtime.Value);
                return AwaitReplyAsync(reply.Task, runtime, CancellationToken.None);
            }
        });
    }

    public Observable<SessionOutput> SessionOutput => _outputSubject.AsObservable();
    public Observable<DaemonConnectionEvent> ConnectionEvents => _connectionSubject.AsObservable();
    public bool IsConnected => _transport.IsConnected;
    internal bool HasLocalRuntime => _runtime.IsValueCreated && _runtime.Value.IsCompletedSuccessfully;
    public ChatCloseReceipt? CloseReceipt => _close.IsValueCreated && _close.Value.IsCompletedSuccessfully ? _close.Value.Result : null;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Post(new Connect(cancellationToken));
    public Task<string> CreateSessionAsync(ChannelType channelType, CancellationToken cancellationToken = default)
        => SessionRequestAsync(new Create(channelType, cancellationToken));
    public Task<string> EnsureSessionAsync(ChannelType channelType, CancellationToken cancellationToken = default)
        => SessionRequestAsync(new Keep(channelType, cancellationToken));
    public Task<string> ResumeSessionAsync(string sessionId, ChannelType channelType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return SessionRequestAsync(new Resume(new SessionId(sessionId), channelType, cancellationToken));
    }
    internal Task OpenChatAsync(string? resumeSessionId, string? initialMessage)
        => Post(new Open(resumeSessionId is null ? null : new SessionId(resumeSessionId), initialMessage));
    public Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        // Refuse before posting: an over-limit frame makes the server close the connection and the text is lost.
        if (SessionHubFrame.CheckText(text) is { } tooLarge)
            return Task.FromException(new InvalidOperationException(tooLarge));
        return Post(new SendText(text, cancellationToken));
    }
    public Task RespondToInteractionAsync(string callId, string selectedKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedKey);
        return Post(new Respond(new ToolCallId(callId), new ApprovalOptionKey(selectedKey), cancellationToken));
    }
    public Task<ChatCloseReceipt> CloseAsync() => _close.Value;

    private async Task<string> SessionRequestAsync(SessionRequest request)
    {
        var runtime = await PostRequest(request).WaitAsync(request.Token).ConfigureAwait(false);
        return (await AwaitReplyAsync(request.Reply.Task, runtime, request.Token).ConfigureAwait(false)).Value;
    }
    private async Task Post(Command command)
    {
        var runtime = await PostRequest(command).WaitAsync(command.Token).ConfigureAwait(false);
        await AwaitReplyAsync(command.Reply.Task, runtime, command.Token).ConfigureAwait(false);
    }
    private Task<Runtime> PostRequest(Request request)
    {
        request.Token.ThrowIfCancellationRequested();
        lock (_runtime)
        {
            ObjectDisposedException.ThrowIf(_lifetime == Lifetime.Disposed, this);
            if (_lifetime == Lifetime.Closing) throw new InvalidOperationException("The chat client is closed.");
            _pending.Enqueue(request);
            return PostAfterAsync(_runtime.Value);
        }
    }
    private async Task<Runtime> PostAfterAsync(Task<Runtime> startup)
    {
        Runtime runtime;
        try { runtime = await startup.ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_runtime)
            {
                while (_pending.TryDequeue(out var request)) request.Fail(error);
                _startupCloseTimer?.Dispose();
            }
            throw;
        }
        lock (_runtime)
        {
            if (_startupClose is { } close)
            {
                if (_clock.GetElapsedTime(close.StartedAt) >= TimeSpan.FromSeconds(2)) ExpireStartup(close);
                if (!close.Reply.Task.IsCompleted)
                {
                    ObjectDisposedException.ThrowIf(_lifetime == Lifetime.Disposed, this);
                    if (runtime.Stopped.IsCompleted) throw new InvalidOperationException("The chat client actor stopped.");
                    // The actor receives the original requests and the remaining quit budget together.
                    _startupClose = null;
                    _pending = new Queue<Request>();
                    _startupCloseTimer?.Dispose();
                    runtime.Owner.Tell(close, ActorRefs.NoSender);
                    return runtime;
                }
            }
            else
            {
                ObjectDisposedException.ThrowIf(_lifetime == Lifetime.Disposed, this);
                if (runtime.Stopped.IsCompleted) throw new InvalidOperationException("The chat client actor stopped.");
                while (_pending.TryDequeue(out var request)) runtime.Owner.Tell(request, ActorRefs.NoSender);
                return runtime;
            }
        }
        await runtime.System.Terminate().ConfigureAwait(false);
        return runtime;
    }
    private void ExpireStartup(Close close)
    {
        lock (_runtime)
        {
            if (!ReferenceEquals(_startupClose, close) || close.Reply.Task.IsCompleted) return;
            var error = new OperationCanceledException("The chat closed before admission confirmation.");
            var inputs = new List<UndeliveredInput>();
            while (_pending.TryDequeue(out var request))
            {
                var text = request switch
                {
                    SendText send => send.Text,
                    Open open => open.Initial,
                    Respond respond => $"Interaction {respond.CallId}: {respond.Selection}",
                    _ => null
                };
                if (text is not null && !request.Token.IsCancellationRequested)
                    inputs.Add(new UndeliveredInput(text, InputDeliveryStatus.Unsent, error.Message));
                request.Fail(error);
            }
            close.Reply.TrySetResult(new ChatCloseReceipt(null, [.. inputs]));
            _startupCloseTimer?.Dispose();
        }
    }
    private static async Task<T> AwaitReplyAsync<T>(Task<T> reply, Task<Runtime> runtime, CancellationToken token)
    {
        await Task.WhenAny(reply, runtime).WaitAsync(token).ConfigureAwait(false);
        if (reply.IsCompleted) return await reply.ConfigureAwait(false);
        return await AwaitReplyAsync(reply, await runtime.ConfigureAwait(false), token).ConfigureAwait(false);
    }
    private static async Task<T> AwaitReplyAsync<T>(Task<T> reply, Runtime runtime, CancellationToken token)
    {
        var completed = await Task.WhenAny(reply, runtime.Stopped).WaitAsync(token).ConfigureAwait(false);
        if (completed != reply) throw new InvalidOperationException("The chat client actor stopped before it returned a result.");
        return await reply.ConfigureAwait(false);
    }
    private static async Task AwaitReplyAsync(Task reply, Runtime runtime, CancellationToken token)
    {
        var completed = await Task.WhenAny(reply, runtime.Stopped).WaitAsync(token).ConfigureAwait(false);
        if (completed != reply) throw new InvalidOperationException("The chat client actor stopped before it returned a result.");
        await reply.ConfigureAwait(false);
    }
    private void OnTransportEvent(TransportEvent value)
    {
        switch (value)
        {
            case OutputReceived output: _events.Writer.TryWrite(new Output(output.Value)); break;
            case TransportDropped drop:
                if (HasLocalRuntime) _runtime.Value.Result.Owner.Tell(drop, ActorRefs.NoSender);
                break;
            default: throw new InvalidOperationException("Unknown daemon transport event.");
        }
    }
    private async Task EventPumpAsync()
    {
        // Subscriber code runs outside the actor and cannot block its close deadline.
        await foreach (var value in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (value)
            {
                case ConnectionChanged connection: _connectionSubject.OnNext(connection.Value); break;
                case Output output: _outputSubject.OnNext(output.Value); break;
                default: throw new InvalidOperationException("Unknown chat client event.");
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task<Runtime>? startup;
        lock (_runtime)
        {
            if (_lifetime == Lifetime.Disposed) return;
            _lifetime = Lifetime.Disposed;
            startup = _runtime.IsValueCreated ? _runtime.Value : null;
            if (_startupClose is { } close) ExpireStartup(close);
            _startupCloseTimer?.Dispose();
        }
        _transportSubscription.Dispose();
        // Callers receive the cached startup failure. Disposal must still release the transport and observers.
        if (startup is not null) await ((Task)startup).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var runtime = startup is { IsCompletedSuccessfully: true } ? startup.Result : null;
        if (runtime is not null) await runtime.System.Terminate().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        if (runtime is not null) await runtime.Pump.ConfigureAwait(false);
        _outputSubject.Dispose();
        _connectionSubject.Dispose();
    }
    private static string NormalizeEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return endpoint.TrimEnd('/');
    }
    internal static SessionOutput FromDto(SessionOutputDto dto) => SessionOutputDtoMapper.FromDto(dto);
    private sealed record Runtime(ActorSystem System, IActorRef Owner, Task Stopped, Task Pump);
}
