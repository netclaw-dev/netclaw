// -----------------------------------------------------------------------
// <copyright file="ChatClientActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Net;
using System.Threading.Channels;
using Akka.Actor;
using Microsoft.AspNetCore.SignalR;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Cli.Daemon.ChatClientProtocol;

namespace Netclaw.Cli.Daemon;

public enum InputDeliveryStatus { Unsent, Rejected, Unconfirmed }
public sealed record UndeliveredInput(string Text, InputDeliveryStatus Status, string Reason)
{
    public string? SessionId { get; init; }
}
public sealed record ChatCloseReceipt(string? SessionId, ImmutableArray<UndeliveredInput> Inputs)
{
    public string Notice => Inputs.Length == 0 ? "All submitted text has daemon admission confirmation."
        : $"{Inputs.Count(input => input.Status == InputDeliveryStatus.Unsent)} unsent; "
        + $"{Inputs.Count(input => input.Status == InputDeliveryStatus.Rejected)} rejected; "
        + $"{Inputs.Count(input => input.Status == InputDeliveryStatus.Unconfirmed)} delivery unconfirmed. "
        + $"Check session {string.Join(", ", Inputs.Where(input => input.Status == InputDeliveryStatus.Unconfirmed).Select(input => input.SessionId ?? "(not created)").Distinct().DefaultIfEmpty(SessionId ?? "(not created)"))} before you resend.";
}

internal sealed class ChatClientActor : ReceiveActor, IWithTimers
{
    private enum RpcKind { Connect, Bind, Send, Respond }
    private enum RetryMode { Explicit, Interactive }
    private abstract record Attachment
    {
        internal abstract SessionId? Id { get; }
    }
    private sealed record NoSession : Attachment { internal override SessionId? Id => null; }
    private sealed record Detached(SessionId SessionId) : Attachment { internal override SessionId? Id => SessionId; }
    private sealed record Attached(SessionId SessionId) : Attachment { internal override SessionId? Id => SessionId; }
    private sealed record RecoveryPending(SessionId SessionId) : Attachment { internal override SessionId? Id => SessionId; }
    private sealed record SessionContext(ChannelType Channel, RetryMode Mode, bool ConnectedBefore, string? Initial, Attachment Attachment);

    private abstract record Work;
    private sealed record Idle : Work;
    private sealed record Pending(Request Request, int Attempt) : Work;
    private sealed record Running(Pending Pending, RpcAttempt Rpc) : Work;
    private sealed record Retrying(Pending Pending) : Work;
    private sealed record AwaitingCancelledRpc(RpcAttempt Rpc) : Work;
    private abstract record CloseState;
    private sealed record OpenClose : CloseState;
    private sealed record Closing(TaskCompletionSource<ChatCloseReceipt> Reply) : CloseState;
    private sealed record Closed(ChatCloseReceipt Receipt) : CloseState;

    // State records reference this owner. Record copies must not create a second resource owner.
    private sealed class RpcAttempt(long id, RpcKind kind, CancellationToken token, IActorRef self) : IDisposable
    {
        internal long Id { get; } = id;
        internal RpcKind Kind { get; } = kind;
        internal bool Dispatched => Kind is RpcKind.Send or RpcKind.Respond;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        private readonly CancellationTokenRegistration _caller = token.Register(() => self.Tell(new Cancelled(id)));
        public void Dispose() { _caller.Dispose(); Cancellation.Dispose(); }
    }

    private static readonly object RetryKey = new();
    private static readonly object OperationKey = new();
    private static readonly object CloseKey = new();
    private static readonly TimeSpan[] AttachmentDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    private readonly IDaemonHubTransport _transport;
    private readonly string _endpoint;
    private readonly TimeSpan[] _delays;
    private readonly TimeSpan _rpcTimeout;
    private readonly TimeProvider _clock;
    private readonly ChannelWriter<ClientEvent> _events;
    private readonly TaskCompletionSource _stopped;
    private readonly Queue<Request> _queue = new();
    private readonly List<UndeliveredInput> _unresolved = [];
    private SessionContext _session = new(ChannelType.Tui, RetryMode.Explicit, false, null, new NoSession());
    private Work _work = new Idle();
    private CloseState _close = new OpenClose();
    private long _operationId;
    public ITimerScheduler Timers { get; set; } = null!;

    public ChatClientActor(IDaemonHubTransport transport, string endpoint, TimeSpan[] delays,
        TimeSpan rpcTimeout, TimeProvider clock, ChannelWriter<ClientEvent> events, TaskCompletionSource stopped)
    {
        _transport = transport;
        _endpoint = endpoint;
        _delays = delays.ToArray();
        _rpcTimeout = rpcTimeout;
        _clock = clock;
        _events = events;
        _stopped = stopped;
        Configure(_work);
    }

    private void Enter(Work work) { _work = work; Become(() => Configure(work)); }
    private void Configure(Work work)
    {
        Controls();
        switch (work)
        {
            case Running active:
                Receive<Completed>(result => CompleteOperation(active, result));
                Receive<Deadline>(deadline => Abort(deadline.Id,
                    new OperationCanceledException("The daemon response timed out. Delivery may be unconfirmed.")));
                break;
            case AwaitingCancelledRpc cancelled:
                Receive<Completed>(result =>
                {
                    if (result.Id != cancelled.Rpc.Id) return;
                    Timers.Cancel(OperationKey);
                    cancelled.Rpc.Dispose();
                    Enter(new Idle());
                    Next();
                });
                Receive<Deadline>(_ => { });
                break;
            case Retrying retry:
                Receive<Retry>(_ => Continue(retry.Pending));
                break;
        }
    }

    private void Controls()
    {
        Receive<Request>(request =>
        {
            if (_close is not OpenClose) request.Fail(new InvalidOperationException("The chat client is closed."));
            else { _queue.Enqueue(request); Next(); }
        });
        Receive<Close>(close =>
        {
            if (_close is Closed closed) { close.Reply.TrySetResult(closed.Receipt); return; }
            if (_close is Closing)
            {
                close.Reply.TrySetException(new InvalidOperationException("The facade must share its close task."));
                return;
            }
            _close = new Closing(close.Reply);
            Timers.StartSingleTimer(CloseKey, new CloseDeadline(), TimeSpan.FromSeconds(2));
            Publish(DaemonConnectionState.Closing, "Confirming daemon admission...");
            Next();
        });
        Receive<CloseDeadline>(_ => FinishClose());
        Receive<TransportDropped>(drop =>
        {
            if (_transport.IsConnected || _close is Closed) return;
            _session = _session with { Attachment = _session.Attachment.Id is { } id ? new RecoveryPending(id) : new NoSession() };
            Publish(DaemonConnectionState.TransportClosed, $"Connection to daemon dropped: {drop.Error?.Message ?? "connection closed"}");
            Next();
        });
        Receive<Cancelled>(cancel => Abort(cancel.Id, new OperationCanceledException("The request was cancelled. Delivery may be unconfirmed.")));
    }

    private void Next()
    {
        if (_work is not Idle || _close is Closed) return;
        if (_session.Attachment is RecoveryPending recovery && _close is OpenClose)
        {
            _session = _session with { Attachment = new Detached(recovery.SessionId) };
            Begin(new Recover());
            return;
        }
        while (_queue.TryDequeue(out var request))
        {
            if (request.Token.IsCancellationRequested) { request.Cancel(); continue; }
            Begin(request);
            return;
        }
        if (_session.Initial is { } initial)
        {
            _session = _session with { Initial = null };
            Begin(new SendText(initial, CancellationToken.None));
            return;
        }
        if (_close is Closing) FinishClose();
    }

    private void Begin(Request request)
    {
        if (request is Open open)
            _session = _session with
            {
                Channel = ChannelType.Tui,
                Mode = RetryMode.Interactive,
                Initial = open.Initial,
                Attachment = Select(open.ResumeId)
            };
        else if (request is SessionRequest selection)
            _session = _session with
            {
                Channel = selection.Channel,
                Mode = RetryMode.Explicit,
                Attachment = request switch { Create => new NoSession(), Resume resume => new Detached(resume.SessionId), _ => _session.Attachment }
            };
        Continue(new Pending(request, 0));
    }
    private static Attachment Select(SessionId? id) => id is { } value ? new Detached(value) : new NoSession();
    private void Detach()
    {
        if (_session.Attachment is RecoveryPending) return;
        _session = _session with { Attachment = Select(_session.Attachment.Id) };
    }

    private void Continue(Pending pending)
    {
        Enter(pending);
        if (pending.Request.Token.IsCancellationRequested) { End(pending, new OperationCanceledException(pending.Request.Token)); return; }
        if (!_transport.IsConnected)
        {
            Detach();
            Publish(_session.ConnectedBefore ? DaemonConnectionState.Reconnecting : DaemonConnectionState.Connecting,
                $"Connecting to daemon at {_endpoint}...");
            Start(pending, RpcKind.Connect, async (id, token) =>
            { await _transport.StartAsync(token).ConfigureAwait(false); return new Connected(id); });
            return;
        }
        if (pending.Request is Connect || pending.Request is Open && _session.Attachment.Id is null && _session.Initial is null)
        { End(pending); return; }
        if (_session.Attachment is Attached attached && pending.Request is not SessionRequest)
        { Dispatch(pending, attached); return; }
        if (_session.Mode == RetryMode.Explicit && _session.Attachment.Id is null && pending.Request is SendText or Respond)
        { End(pending, new InvalidOperationException("Session not initialized. Call CreateSessionAsync first.")); return; }
        var session = _session;
        Start(pending, RpcKind.Bind, async (id, token) => new Bound(id,
            await _transport.EnsureSessionAsync(session.Attachment.Id, session.Channel, token).ConfigureAwait(false)));
    }
    private void Dispatch(Pending pending, Attached attachment)
    {
        switch (pending.Request)
        {
            case SendText text:
                Start(pending, RpcKind.Send, async (id, token) =>
                { await _transport.SendAsync(attachment.SessionId, text.Text, token).ConfigureAwait(false); return new Accepted(id); });
                break;
            case Respond response:
                Start(pending, RpcKind.Respond, async (id, token) =>
                { await _transport.RespondAsync(attachment.SessionId, response.CallId, response.Selection, token).ConfigureAwait(false); return new Accepted(id); });
                break;
            default: End(pending); break;
        }
    }

    private void Start(Pending pending, RpcKind kind, Func<long, CancellationToken, Task<Completed>> operation)
    {
        var rpc = new RpcAttempt(++_operationId, kind, pending.Request.Token, Self);
        Timers.StartSingleTimer(OperationKey, new Deadline(rpc.Id), _rpcTimeout);
        Enter(new Running(pending, rpc));
        Task<Completed> task;
        try { task = operation(rpc.Id, rpc.Cancellation.Token); }
        catch (Exception error) { task = Task.FromException<Completed>(error); }
        task.PipeTo(Self, success: result => result, failure: error => new Failed(rpc.Id, error));
    }

    private void CompleteOperation(Running active, Completed result)
    {
        if (result.Id != active.Rpc.Id) return;
        Timers.Cancel(OperationKey);
        active.Rpc.Dispose();
        var pending = active.Pending;
        Enter(pending);
        if (result is Failed failed)
        {
            var error = failed.Error;
            if (error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                error = new InvalidOperationException("Authentication failed. Run 'netclaw pair <endpoint>' to re-pair this device.", error);
            else if (active.Rpc.Kind is RpcKind.Connect or RpcKind.Bind && !pending.Request.Token.IsCancellationRequested)
            {
                var limit = pending.Request is Recover ? 20 : _session.Mode == RetryMode.Interactive ? int.MaxValue : _delays.Length;
                var attempt = pending.Attempt + 1;
                if (attempt < limit)
                {
                    var interactiveBind = active.Rpc.Kind == RpcKind.Bind && _session.Mode == RetryMode.Interactive;
                    var delays = interactiveBind ? AttachmentDelays : _delays;
                    var index = interactiveBind ? attempt - 1 : attempt;
                    var delay = delays[Math.Min(index, delays.Length - 1)];
                    Enter(new Retrying(pending with { Attempt = attempt }));
                    Timers.StartSingleTimer(RetryKey, new Retry(), delay);
                    Publish(DaemonConnectionState.Reconnecting, $"Retry {attempt} in {delay.TotalSeconds:0}s: {error.Message}", attempt, limit);
                    return;
                }
            }
            End(pending, error, active.Rpc.Dispatched);
            return;
        }
        switch (result)
        {
            case Connected when active.Rpc.Kind == RpcKind.Connect:
                if (!_transport.IsConnected) { Continue(pending); return; }
                _session = _session with { ConnectedBefore = true };
                Publish(DaemonConnectionState.Connected, $"Connected to daemon at {_endpoint}.");
                Continue(pending);
                break;
            case Bound bound when active.Rpc.Kind == RpcKind.Bind:
                if (bound.Binding.AdmissionVersion != SessionEnsureResultDto.SupportedTextAdmissionVersion)
                {
                    Detach();
                    End(pending, new NotSupportedException($"The daemon has unsupported text admission version {bound.Binding.AdmissionVersion}. Upgrade the daemon."));
                    return;
                }
                ArgumentException.ThrowIfNullOrWhiteSpace(bound.Binding.SessionId.Value);
                _session = _session with
                {
                    Attachment = _session.Attachment is RecoveryPending
                    ? new RecoveryPending(bound.Binding.SessionId) : new Detached(bound.Binding.SessionId)
                };
                if (!_transport.IsConnected) { Continue(pending); return; }
                var attached = new Attached(bound.Binding.SessionId);
                _session = _session with { Attachment = attached };
                pending = pending with { Attempt = 0 };
                Publish(DaemonConnectionState.Connected, $"{(pending.Request is Recover ? "Reconnected" : "Attached")} to daemon session {attached.SessionId}.");
                Dispatch(pending, attached);
                break;
            case Accepted when active.Rpc.Dispatched: End(pending); break;
            default: throw new InvalidOperationException("The RPC result does not match its operation.");
        }
    }

    private void Abort(long id, Exception error)
    {
        if (id != _operationId || _close is Closed) return;
        if (_work is Running active)
        {
            RecordFailure(active.Pending.Request, error, active.Rpc.Dispatched ? InputDeliveryStatus.Unconfirmed : InputDeliveryStatus.Unsent);
            active.Rpc.Cancellation.Cancel();
            // Cancellation alone does not end transport side effects.
            Enter(new AwaitingCancelledRpc(active.Rpc));
            active.Pending.Request.Fail(error);
        }
        else if (_work is Retrying retry) End(retry.Pending, error);
    }

    private void End(Pending pending, Exception? error = null, bool dispatched = false)
    {
        var request = pending.Request;
        Timers.Cancel(RetryKey);
        if (error is not null)
        {
            var status = dispatched
                ? error is HubException && error.Message.Contains(SessionEnsureResultDto.TextRejectionPrefix, StringComparison.Ordinal)
                    ? InputDeliveryStatus.Rejected : InputDeliveryStatus.Unconfirmed
                : InputDeliveryStatus.Unsent;
            RecordFailure(request, error, status);
            if (request is SendText) Detach();
            request.Fail(error);
            if (request is Open or Recover || error is NotSupportedException)
                Publish(DaemonConnectionState.Disconnected, error.Message);
            if (request is Open && _session.Initial is { } initial)
            {
                RecordFailure(new SendText(initial, CancellationToken.None), error, InputDeliveryStatus.Unsent);
                _session = _session with { Initial = null };
            }
        }
        else if (request is SessionRequest selection)
            selection.Reply.TrySetResult(_session.Attachment.Id ?? throw new InvalidOperationException("The session command completed without an identity."));
        else if (request is Command command) command.Reply.TrySetResult();
        Enter(new Idle());
        Next();
    }
    private void RecordFailure(Request request, Exception error, InputDeliveryStatus status)
    {
        var text = request switch
        {
            SendText send => send.Text,
            Respond respond => $"Interaction {respond.CallId}: {respond.Selection}",
            _ => null
        };
        if (text is null) return;
        var sessionId = _session.Attachment.Id?.Value;
        _unresolved.Add(new UndeliveredInput(text, status, error.Message) { SessionId = sessionId });
        if (request is Respond) return;
        _events.TryWrite(new Output(new ErrorOutput
        {
            SessionId = new SessionId(sessionId ?? "signalr/client"),
            TimestampMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            Message = $"Message {status.ToString().ToLowerInvariant()}: {error.Message}"
        }));
    }
    private void FinishClose()
    {
        if (_close is Closed) return;
        Timers.CancelAll();
        var error = new OperationCanceledException("The chat closed before admission confirmation.");
        switch (_work)
        {
            case Running active:
                RecordFailure(active.Pending.Request, error, active.Rpc.Dispatched ? InputDeliveryStatus.Unconfirmed : InputDeliveryStatus.Unsent);
                active.Pending.Request.Fail(error);
                active.Rpc.Cancellation.Cancel();
                Enter(new AwaitingCancelledRpc(active.Rpc));
                break;
            case Pending pending: pending.Request.Fail(error); RecordFailure(pending.Request, error, InputDeliveryStatus.Unsent); Enter(new Idle()); break;
            case Retrying retry: retry.Pending.Request.Fail(error); RecordFailure(retry.Pending.Request, error, InputDeliveryStatus.Unsent); Enter(new Idle()); break;
        }
        while (_queue.TryDequeue(out var pending))
        {
            if (!pending.Token.IsCancellationRequested) RecordFailure(pending, error, InputDeliveryStatus.Unsent);
            pending.Fail(error);
        }
        if (_session.Initial is { } initial)
            _unresolved.Add(new UndeliveredInput(initial, InputDeliveryStatus.Unsent, error.Message) { SessionId = _session.Attachment.Id?.Value });
        _session = _session with { Initial = null };
        var receipt = new ChatCloseReceipt(_session.Attachment.Id?.Value, _unresolved.ToImmutableArray());
        if (_close is Closing closing) closing.Reply.TrySetResult(receipt);
        _close = new Closed(receipt);
    }
    private void Publish(DaemonConnectionState state, string message, int? attempt = null, int? limit = null)
        => _events.TryWrite(new ConnectionChanged(new DaemonConnectionEvent(state, _endpoint, message, attempt, limit) { SessionId = _session.Attachment.Id?.Value }));
    protected override void PostStop()
    {
        FinishClose();
        if (_work is AwaitingCancelledRpc cancelled) cancelled.Rpc.Dispose();
        _stopped.TrySetResult();
        base.PostStop();
    }
}
