// -----------------------------------------------------------------------
// <copyright file="DaemonClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using R3;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Thin SignalR client for daemon-backed sessions.
/// Maintains connection state, session attachment across reconnects,
/// and exposes mapped <see cref="SessionOutput"/> events for the TUI.
/// </summary>
/// <remarks>
/// <para>
/// The client runs a single owner loop over a command mailbox. The loop is the
/// ONLY code that touches the transport, <c>_sessionId</c>, and
/// <c>_channelType</c>. Public methods and the transport <c>Closed</c> callback
/// post commands; the loop runs one at a time. This removes the locks the older
/// design needed to referee two reconnect authorities and a foreground caller
/// all racing for one connection.
/// </para>
/// <para>
/// Two single-writer event streams sit outside the loop and stay lock-free:
/// the owner is the only writer of <see cref="ConnectionEvents"/>, and the
/// transport's serialized <c>ReceiveOutput</c> callback is the only writer of
/// <see cref="SessionOutput"/>.
/// </para>
/// </remarks>
public sealed class DaemonClient : IAsyncDisposable
{
    public static readonly ChannelType TuiChannelType = ChannelType.Tui;

    internal static readonly TimeSpan[] DefaultReconnectDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    // Upper bound on any single hub RPC. It is a backstop, not the primary
    // liveness signal (SignalR ServerTimeout still applies on the real
    // transport). It guarantees a lost response can never park a caller
    // indefinitely — the failure mode that hung the TUI in the old design.
    internal static readonly TimeSpan DefaultRpcTimeout = TimeSpan.FromSeconds(60);

    // Bounds the background reconnect after a drop before it gives up with a
    // terminal Disconnected. The initial/lazy connect uses the shorter
    // _reconnectDelays pass instead.
    private const int MaxReconnectAttempts = 20;

    private readonly IDaemonHubTransport _transport;
    private readonly string _daemonEndpoint;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan[] _reconnectDelays;
    private readonly TimeSpan _rpcTimeout;
    private readonly Subject<SessionOutput> _outputSubject = new();
    private readonly Subject<DaemonConnectionEvent> _connectionSubject = new();
    private readonly Channel<ClientCommand> _mailbox;
    private readonly Channel<DaemonConnectionEvent> _eventChannel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDisposable _outputRegistration;
    private readonly Task _ownerTask;
    private readonly Task _eventPumpTask;

    // Owner-only state — read and written solely inside the owner loop, so it
    // needs no lock or volatile.
    private string? _sessionId;
    private ChannelType? _channelType;
    private bool _hasConnected;
    private bool _sessionAttached;

    private volatile bool _disposed;

    public DaemonClient(
        string daemonEndpoint,
        TimeProvider? timeProvider = null,
        TimeSpan[]? reconnectDelays = null,
        TimeSpan? serverTimeout = null,
        Func<Task<string?>>? accessTokenProvider = null)
        : this(
            daemonEndpoint,
            SignalRDaemonHubTransport.Create(
                BuildHubUrl(NormalizeEndpoint(daemonEndpoint)),
                accessTokenProvider,
                serverTimeout),
            timeProvider,
            reconnectDelays,
            rpcTimeout: null)
    {
    }

    /// <summary>
    /// Test seam: injects a controllable transport so the reconnect and session
    /// state machine runs without real SignalR or sockets.
    /// </summary>
    internal DaemonClient(
        string daemonEndpoint,
        IDaemonHubTransport transport,
        TimeProvider? timeProvider = null,
        TimeSpan[]? reconnectDelays = null,
        TimeSpan? rpcTimeout = null)
    {
        _daemonEndpoint = NormalizeEndpoint(daemonEndpoint);
        _transport = transport;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reconnectDelays = reconnectDelays ?? DefaultReconnectDelays;
        _rpcTimeout = rpcTimeout ?? DefaultRpcTimeout;
        _mailbox = Channel.CreateUnbounded<ClientCommand>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _eventChannel = Channel.CreateUnbounded<DaemonConnectionEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        _outputRegistration = _transport.On<SessionOutputDto>(
            "ReceiveOutput",
            dto => _outputSubject.OnNext(FromDto(dto)));
        _transport.Closed += OnTransportClosed;

        _ownerTask = Task.Run(RunAsync);
        _eventPumpTask = Task.Run(EventPumpAsync);
    }

    public Observable<SessionOutput> SessionOutput => _outputSubject.AsObservable();

    /// <summary>
    /// Connection lifecycle events. They are delivered on a dedicated pump, not
    /// the client's command loop, so a subscriber that blocks or reenters the
    /// client cannot stall daemon calls for other callers. Delivery is still
    /// synchronous per event — offload heavy work onto a scheduler if needed.
    /// </summary>
    public Observable<DaemonConnectionEvent> ConnectionEvents => _connectionSubject.AsObservable();

    public bool IsConnected => _transport.IsConnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await PostAsync(new ConnectCommand(ack, cancellationToken), cancellationToken);
        await ack.Task.WaitAsync(cancellationToken);
    }

    public Task<string> CreateSessionAsync(
        ChannelType channelType,
        CancellationToken cancellationToken = default)
        => EnsureSessionAsync(channelType, SessionInit.Create, cancellationToken);

    public Task<string> EnsureSessionAsync(
        ChannelType channelType,
        CancellationToken cancellationToken = default)
        => EnsureSessionAsync(channelType, SessionInit.Keep, cancellationToken);

    /// <summary>
    /// Sets the session ID for subsequent calls so that <c>EnsureSession</c>
    /// attaches to (or rehydrates) an existing session instead of creating a new one.
    /// </summary>
    public Task<string> ResumeSessionAsync(
        string sessionId,
        ChannelType channelType,
        CancellationToken cancellationToken = default)
        => EnsureSessionAsync(channelType, SessionInit.Attach(sessionId), cancellationToken);

    private async Task<string> EnsureSessionAsync(
        ChannelType channelType,
        SessionInit init,
        CancellationToken cancellationToken)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await PostAsync(new EnsureSessionCommand(channelType, init, reply, cancellationToken), cancellationToken);
        return await reply.Task.WaitAsync(cancellationToken);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Only non-empty text messages are currently supported.");

        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await PostAsync(new SendCommand(text, ack, cancellationToken), cancellationToken);
        await ack.Task.WaitAsync(cancellationToken);
    }

    public async Task RespondToInteractionAsync(
        string callId,
        string selectedKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedKey);

        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await PostAsync(new RespondCommand(callId, selectedKey, ack, cancellationToken), cancellationToken);
        await ack.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Stop accepting commands, then cancel the loop and every in-flight
        // await. Await the owner before touching the transport or the subjects
        // so nothing races the teardown — the owner is the only writer of both.
        _mailbox.Writer.TryComplete();
        _lifetime.Cancel();

        // RunAsync ends normally when the writer completes; it never rethrows
        // cancellation, so awaiting it here cannot throw. Await it before
        // completing the event channel so no late Publish is dropped.
        await _ownerTask.ConfigureAwait(false);

        _eventChannel.Writer.TryComplete();
        await _eventPumpTask.ConfigureAwait(false);

        _outputRegistration.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
        _outputSubject.Dispose();
        _connectionSubject.Dispose();
        _lifetime.Dispose();
    }

    private Task OnTransportClosed(Exception? error)
    {
        // Runs on a transport callback thread. Only hand off to the owner.
        _mailbox.Writer.TryWrite(new TransportDroppedCommand(error));
        return Task.CompletedTask;
    }

    private async Task PostAsync(ClientCommand command, CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DaemonClient));

        try
        {
            await _mailbox.Writer.WriteAsync(command, cancellationToken);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(DaemonClient));
        }
    }

    // ----- the single owner loop -----

    private async Task RunAsync()
    {
        // The reader ends when DisposeAsync completes the writer, so the loop
        // needs no cancellation token. A command already in flight when dispose
        // cancels _lifetime faults fast through the per-command catch below.
        try
        {
            await foreach (var command in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(command).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    Fault(command, new ObjectDisposedException(nameof(DaemonClient)));
                    break;
                }
                catch (Exception ex)
                {
                    // Fault only this caller; the owner keeps serving the mailbox.
                    Fault(command, ex);
                }
            }
        }
        finally
        {
            // Fault anything still queued so no caller waits forever.
            while (_mailbox.Reader.TryRead(out var pending))
                Fault(pending, new ObjectDisposedException(nameof(DaemonClient)));
        }
    }

    private async Task ProcessAsync(ClientCommand command)
    {
        switch (command)
        {
            case ConnectCommand c:
            {
                using var op = LinkOperation(c.Token);
                await EnsureConnectedAsync(op.Token);
                c.Ack.TrySetResult();
                break;
            }

            case EnsureSessionCommand c:
            {
                using var op = LinkOperation(c.Token);
                c.Reply.TrySetResult(await EnsureSessionCoreAsync(c, op.Token));
                break;
            }

            case SendCommand c:
            {
                using var op = LinkOperation(c.Token);
                await EnsureConnectedAsync(op.Token);
                await ReattachIfNeededAsync(op.Token);
                await InvokeAsync("SendMessage", [RequireSession(), c.Text], op.Token);
                c.Ack.TrySetResult();
                break;
            }

            case RespondCommand c:
            {
                using var op = LinkOperation(c.Token);
                await EnsureConnectedAsync(op.Token);
                await ReattachIfNeededAsync(op.Token);
                await InvokeAsync("RespondToInteraction", [RequireSession(), c.CallId, c.SelectedKey], op.Token);
                c.Ack.TrySetResult();
                break;
            }

            case TransportDroppedCommand c:
                await HandleTransportDroppedAsync(c.Error);
                break;
        }
    }

    // Links the caller's token with the client lifetime so the owner aborts an
    // in-flight hub call when the caller cancels — the behavior the old client
    // had by threading the token straight into InvokeCoreAsync.
    private CancellationTokenSource LinkOperation(CancellationToken callerToken)
        => CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, callerToken);

    private async Task<string> EnsureSessionCoreAsync(EnsureSessionCommand command, CancellationToken operationToken)
    {
        ApplyInit(command.Init);
        _channelType = command.ChannelType;

        await EnsureConnectedAsync(operationToken);

        var result = await InvokeAsync<SessionEnsureResultDto>(
            "EnsureSession",
            [_sessionId, command.ChannelType.ToWireValue()],
            operationToken);

        _sessionId = result.SessionId;
        _sessionAttached = true;

        if (result.Created)
        {
            Publish(
                DaemonConnectionState.Connected,
                $"Created a new daemon session at {_daemonEndpoint}.");
        }

        return result.SessionId;
    }

    private void ApplyInit(SessionInit init)
    {
        switch (init.Kind)
        {
            case SessionInitKind.Create:
                _sessionId = null;
                break;
            case SessionInitKind.Attach:
                _sessionId = init.SessionId;
                break;
            case SessionInitKind.Keep:
                break;
        }
    }

    /// <summary>
    /// Re-attaches the current session to a freshly (re)connected transport.
    /// A new connection has no server-side session binding, so a session-scoped
    /// RPC would otherwise fail with "session not attached".
    /// </summary>
    private async Task ReattachIfNeededAsync(CancellationToken operationToken)
    {
        if (_sessionAttached)
            return;
        if (_sessionId is null || _channelType is not { } channelType)
            return;

        var result = await InvokeAsync<SessionEnsureResultDto>(
            "EnsureSession",
            [_sessionId, channelType.ToWireValue()],
            operationToken);
        _sessionId = result.SessionId;
        _sessionAttached = true;
    }

    private string RequireSession()
        => _sessionId
           ?? throw new InvalidOperationException("Session not initialized. Call CreateSessionAsync first.");

    /// <summary>
    /// Ensures the transport is up. Emits Connecting/Reconnecting at the start
    /// and Connected on success. Throws on exhaustion so the driving command
    /// faults fast instead of hanging.
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken operationToken)
    {
        if (_transport.IsConnected)
            return;

        var recovery = _hasConnected;

        // A fresh transport connection carries no server-side session binding.
        _sessionAttached = false;

        Publish(
            recovery ? DaemonConnectionState.Reconnecting : DaemonConnectionState.Connecting,
            recovery
                ? $"Reconnecting to daemon at {_daemonEndpoint}..."
                : $"Connecting to daemon at {_daemonEndpoint}...");

        await ConnectThroughDelaysAsync(operationToken);
        _hasConnected = true;

        Publish(
            DaemonConnectionState.Connected,
            recovery
                ? $"Reconnected to daemon at {_daemonEndpoint}."
                : $"Connected to daemon at {_daemonEndpoint}.");
    }

    /// <summary>
    /// One pass over <see cref="_reconnectDelays"/>: delay, then a single
    /// StartAsync. Returns on the first success; throws on exhaustion.
    /// </summary>
    private async Task ConnectThroughDelaysAsync(CancellationToken operationToken)
    {
        Exception? lastError = null;
        foreach (var delay in _reconnectDelays)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _timeProvider, operationToken);

            try
            {
                await _transport.StartAsync(operationToken);
                return;
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsAuthenticationFailure(ex))
            {
                throw new InvalidOperationException(
                    "Authentication failed: the daemon rejected the bearer token. " +
                    "Run 'netclaw pair <endpoint>' to re-pair this device.", ex);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("Failed to connect to daemon SignalR hub.", lastError);
    }

    /// <summary>
    /// The single reconnect authority. Runs after the transport drops: retries
    /// StartAsync with <see cref="_timeProvider"/>-driven backoff, re-attaches
    /// the session, and emits a terminal Disconnected only if it exhausts its
    /// budget.
    /// </summary>
    private async Task HandleTransportDroppedAsync(Exception? error)
    {
        // A foreground command may have already reconnected before this drop
        // notification was processed; then the drop is moot.
        if (_transport.IsConnected)
            return;

        _sessionAttached = false;

        // Report the drop unconditionally, like the old Closed handler — the TUI
        // clears readiness on this even before a session exists.
        Publish(
            DaemonConnectionState.TransportClosed,
            $"Connection to daemon at {_daemonEndpoint} dropped: {error?.Message ?? "connection closed"}");

        // Only the reconnect loop needs a session to re-attach. Without one there
        // is nothing to recover, so leave the transport for the next command.
        if (_sessionId is null || _channelType is not { } channelType)
            return;

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            _lifetime.Token.ThrowIfCancellationRequested();

            if (attempt > 1)
                await Task.Delay(BackoffFor(attempt), _timeProvider, _lifetime.Token);

            Publish(
                DaemonConnectionState.Reconnecting,
                $"Retrying daemon connection at {_daemonEndpoint} (attempt {attempt}/{MaxReconnectAttempts})...",
                attempt,
                MaxReconnectAttempts,
                0);

            try
            {
                // Guard StartAsync: a prior attempt may have connected the
                // transport but failed the re-attach RPC. Calling StartAsync on
                // an already-connected HubConnection throws "not in the
                // Disconnected state" — which would falsely exhaust the budget on
                // a live socket. Retry only the re-attach in that case.
                if (!_transport.IsConnected)
                    await _transport.StartAsync(_lifetime.Token);

                var result = await InvokeAsync<SessionEnsureResultDto>(
                    "EnsureSession",
                    [_sessionId, channelType.ToWireValue()],
                    _lifetime.Token);
                _sessionId = result.SessionId;
                _sessionAttached = true;

                Publish(
                    DaemonConnectionState.Connected,
                    $"Reconnected to daemon at {_daemonEndpoint}.");
                return;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Transient failure — record it, then back off and retry. On the
                // last attempt, surface why the reconnect gave up.
                lastError = ex;
                if (attempt >= MaxReconnectAttempts)
                {
                    Publish(
                        DaemonConnectionState.Disconnected,
                        $"Unable to reconnect to daemon at {_daemonEndpoint} after {MaxReconnectAttempts} attempts: {lastError.Message}",
                        attempt,
                        MaxReconnectAttempts,
                        0);
                    return;
                }
            }
        }
    }

    private TimeSpan BackoffFor(int attempt)
        => _reconnectDelays[Math.Min(attempt - 1, _reconnectDelays.Length - 1)];

    private async Task<TResult> InvokeAsync<TResult>(string method, object?[] args, CancellationToken operationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_rpcTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, timeoutCts.Token);
        return await _transport.InvokeAsync<TResult>(method, args, linked.Token);
    }

    private async Task InvokeAsync(string method, object?[] args, CancellationToken operationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_rpcTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, timeoutCts.Token);
        await _transport.InvokeAsync(method, args, linked.Token);
    }

    // Runs on the owner loop. Events go to a channel drained by EventPumpAsync so
    // subscriber code never executes on the owner thread.
    private void Publish(
        DaemonConnectionState state,
        string message,
        int? attempt = null,
        int? maxAttempts = null,
        int? secondsUntilRetry = null)
    {
        _eventChannel.Writer.TryWrite(new DaemonConnectionEvent(
            state,
            _daemonEndpoint,
            message,
            attempt,
            maxAttempts,
            secondsUntilRetry));
    }

    private async Task EventPumpAsync()
    {
        // Deliver connection events off the owner thread. A subscriber that blocks
        // (or reenters the client) here stalls only this pump, not the command
        // mailbox — so Send/EnsureSession/Respond keep flowing and a reentrant
        // call cannot deadlock the owner. The pump ends when DisposeAsync
        // completes the event channel.
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            _connectionSubject.OnNext(evt);
    }

    private static void Fault(ClientCommand command, Exception ex)
    {
        switch (command)
        {
            case ConnectCommand c:
                c.Ack.TrySetException(ex);
                break;
            case EnsureSessionCommand c:
                c.Reply.TrySetException(ex);
                break;
            case SendCommand c:
                c.Ack.TrySetException(ex);
                break;
            case RespondCommand c:
                c.Ack.TrySetException(ex);
                break;
            case TransportDroppedCommand:
                // No caller awaits a transport-drop notification.
                break;
        }
    }

    private static bool IsAuthenticationFailure(Exception? ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized };

    private static string NormalizeEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Daemon endpoint cannot be empty.", nameof(endpoint));

        return endpoint.TrimEnd('/');
    }

    private static string BuildHubUrl(string endpoint) => $"{endpoint.TrimEnd('/')}/hub/session";

    internal static SessionOutput FromDto(SessionOutputDto dto) => SessionOutputDtoMapper.FromDto(dto);

    // ----- command mailbox contract -----

    private abstract record ClientCommand;

    private sealed record ConnectCommand(TaskCompletionSource Ack, CancellationToken Token) : ClientCommand;

    private sealed record EnsureSessionCommand(
        ChannelType ChannelType,
        SessionInit Init,
        TaskCompletionSource<string> Reply,
        CancellationToken Token) : ClientCommand;

    private sealed record SendCommand(string Text, TaskCompletionSource Ack, CancellationToken Token) : ClientCommand;

    private sealed record RespondCommand(
        string CallId,
        string SelectedKey,
        TaskCompletionSource Ack,
        CancellationToken Token) : ClientCommand;

    private sealed record TransportDroppedCommand(Exception? Error) : ClientCommand;

    private enum SessionInitKind
    {
        Create,
        Keep,
        Attach
    }

    private readonly record struct SessionInit(SessionInitKind Kind, string? SessionId)
    {
        public static readonly SessionInit Create = new(SessionInitKind.Create, null);
        public static readonly SessionInit Keep = new(SessionInitKind.Keep, null);

        public static SessionInit Attach(string sessionId) => new(SessionInitKind.Attach, sessionId);
    }
}
