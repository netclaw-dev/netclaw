// -----------------------------------------------------------------------
// <copyright file="ChannelLifecycleActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging;

namespace Netclaw.Channels;

/// <summary>
/// Shared gateway lifecycle state machine for channels with a WebSocket-style
/// transport (SPEC-015 §1.1): Disconnected → Connecting → Ready, plus
/// Disconnecting. The base owns state transitions, the
/// Connect/Disconnect/GetSnapshot ask protocol, exponential-backoff
/// auto-reconnect (with a stability window gating backoff resets), the
/// optional ready-signal timeout for channels whose transport start does not
/// imply readiness, snapshot health reporting, and the clean-reconnect →
/// stop → auto-reconnect flow. Subclasses supply transport start/stop calls,
/// the snapshot factory, transport event subscriptions, and per-state
/// handlers for channel-specific transport events.
/// </summary>
/// <typeparam name="TSnapshot">Channel snapshot type returned by snapshot and connect asks.</typeparam>
/// <typeparam name="TConnectCommand">
/// The channel's connect command, carrying its transport credentials. Kept as a
/// generic parameter (rather than a shared record) because credential shape
/// differs per channel — Discord needs a bot token, Mattermost a server URL and
/// token — and the per-channel nested <c>Connect</c> records are part of each
/// channel's existing call-site surface.
/// </typeparam>
public abstract class ChannelLifecycleActor<TSnapshot, TConnectCommand> : ReceiveActor
    where TSnapshot : IGatewaySnapshot
    where TConnectCommand : class
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a connection must hold Ready before a subsequent failure
    /// resets the retry backoff to zero. A ready→drop flap inside this window
    /// keeps the previous backoff growing instead of reconnecting with zero
    /// delay forever.
    /// </summary>
    private static readonly TimeSpan BackoffResetStabilityWindow = TimeSpan.FromSeconds(60);

    private readonly string _channelDisplayName;
    private readonly string _disconnectedDetail;
    private readonly string _notReadyDetail;
    private readonly TimeProvider _timeProvider;

    private bool _isReadyBehavior;
    private long _connectAttempt;
    private IActorRef? _pendingConnectReplyTo;
    private string? _healthDetail;
    private bool _cleanReconnectEmitted;
    private ICancelable? _readySignalTimer;
    private DateTimeOffset? _readyEnteredAt;

    private bool _autoReconnect;
    private TConnectCommand? _retryConnectCommand;
    private TimeSpan _retryDelay;
    private ICancelable? _retryTimer;

    /// <remarks>
    /// The constructor calls <see cref="ActorBase.Become"/>, which invokes the
    /// subclass's Register* hooks before the subclass constructor body has run.
    /// Hooks must therefore only register handlers (method groups / lambdas) —
    /// never read subclass fields at registration time.
    /// </remarks>
    protected ChannelLifecycleActor(string channelDisplayName, TimeProvider timeProvider, ILogger logger)
    {
        _channelDisplayName = channelDisplayName;
        _timeProvider = timeProvider;
        Logger = logger;
        _disconnectedDetail = channelDisplayName + " gateway disconnected.";
        _notReadyDetail = channelDisplayName + " gateway connected but not ready.";
        _healthDetail = _disconnectedDetail;

        Become(Disconnected);
    }

    protected ILogger Logger { get; }

    /// <summary>Clock used for backoff stability tracking; also available to subclasses for event timestamps.</summary>
    protected TimeProvider TimeProvider => _timeProvider;

    /// <summary>
    /// Self captured in <see cref="PreStart"/> for use by transport event
    /// callbacks, which run off the actor's dispatcher where <c>Self</c> is
    /// unavailable.
    /// </summary>
    protected IActorRef SelfRef { get; private set; } = ActorRefs.Nobody;

    /// <summary>Operator-facing channel name ("Discord", "Mattermost") used in health details and logs.</summary>
    protected string ChannelDisplayName => _channelDisplayName;

    /// <summary>The health detail reported once a stop has fully completed.</summary>
    protected string DisconnectedDetail => _disconnectedDetail;

    /// <summary>Health detail surfaced by snapshots while not Ready; null when Ready.</summary>
    protected string? HealthDetail
    {
        get => _healthDetail;
        set => _healthDetail = value;
    }

    /// <summary>Monotonic connect-attempt stamp used to ignore stale start/timeout work.</summary>
    protected long CurrentConnectAttempt => _connectAttempt;

    /// <summary>Whether an operator connect ask is awaiting a reply.</summary>
    protected bool HasPendingConnect => _pendingConnectReplyTo is not null;

    /// <summary>True while the transport-level connection is up.</summary>
    protected abstract bool IsTransportConnected { get; }

    /// <summary>True once the channel has resolved the bot's own identity.</summary>
    protected abstract bool HasBotIdentity { get; }

    /// <summary>
    /// How long the actor waits in Connecting for the channel's explicit
    /// ready signal before treating the attempt as a transient failure and
    /// driving a clean reconnect. Null (the default) means a successful
    /// transport start implies readiness (Mattermost) and no timer is armed;
    /// channels with a separate ready event (Discord's READY) override this.
    /// </summary>
    protected virtual TimeSpan? ReadySignalTimeout => null;

    protected override void PreStart()
    {
        SelfRef = Self;
        SubscribeTransportEvents();
        base.PreStart();
    }

    protected override void PostStop()
    {
        UnsubscribeTransportEvents();
        CancelRetryTimer();
        CancelReadySignalTimer();
        base.PostStop();
    }

    private void Disconnected()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<TConnectCommand>(connect =>
        {
            _retryConnectCommand = connect;
            _autoReconnect = true;
            StartConnecting(connect, Sender);
        });
        Receive<Disconnect>(_ =>
        {
            CancelAutoReconnect();
            StartDisconnecting(Sender);
        });
        Receive<RetryConnect>(_ =>
        {
            if (!_autoReconnect || _retryConnectCommand is null)
                return;

            Logger.LogInformation(
                "Attempting {Channel} reconnect (delay was {Delay}).",
                _channelDisplayName, _retryDelay);
            StartConnecting(_retryConnectCommand, ActorRefs.Nobody);
        });
        RegisterDisconnectedChannelHandlers();
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(Disconnected));
    }

    private void Connecting()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<TConnectCommand>(_ => Sender.Tell(new Status.Failure(new InvalidOperationException(
            _channelDisplayName + " gateway connect is already in progress."))));
        Receive<Disconnect>(_ => StartDisconnecting(Sender));
        Receive<StartSucceeded>(HandleStartSucceeded);
        Receive<StartFailed>(HandleStartFailed);
        Receive<ReadySignalTimedOut>(HandleReadySignalTimedOut);
        RegisterConnectingChannelHandlers();
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(Connecting));
    }

    private void Ready()
    {
        _isReadyBehavior = true;
        ReceiveCommon();
        Receive<TConnectCommand>(_ => Sender.Tell(CurrentSnapshot()));
        Receive<Disconnect>(_ => StartDisconnecting(Sender));
        RegisterReadyChannelHandlers();
        ReceiveUnexpected(nameof(Ready));
    }

    private void Disconnecting()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<TConnectCommand>(_ => Sender.Tell(new Status.Failure(new InvalidOperationException(
            _channelDisplayName + " gateway disconnect is already in progress."))));
        Receive<Disconnect>(_ => Sender.Tell(new Status.Failure(new InvalidOperationException(
            _channelDisplayName + " gateway disconnect is already in progress."))));
        Receive<StopSucceeded>(HandleStopSucceeded);
        Receive<StopFailed>(HandleStopFailed);
        RegisterDisconnectingChannelHandlers();
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(Disconnecting));
    }

    private void ReceiveCommon()
    {
        Receive<GetSnapshot>(_ => Sender.Tell(CurrentSnapshot()));
        RegisterCommonChannelHandlers();
        Receive<DispatchFailed>(HandleDispatchFailed);
    }

    private void ReceiveNotReadyIngress() => RegisterNotReadyIngressHandlers();

    private void ReceiveUnexpected(string behaviorName) =>
        ReceiveAny(message => HandleWrongBehaviorMessage(message, behaviorName));

    /// <summary>Protected state-transition entry point for channel-specific flows (fatal closes).</summary>
    protected void BecomeDisconnectedBehavior() => Become(Disconnected);

    /// <summary>
    /// Re-enters Connecting without restarting the transport — fresh attempt
    /// stamp and (when <see cref="ReadySignalTimeout"/> is configured) a fresh
    /// ready-signal timer. Used by channels whose transport reconnects on its
    /// own after a drop (Discord.Net) so the actor waits for the transport's
    /// ready signal instead of tearing the client down.
    /// </summary>
    protected void WaitForReadySignal()
    {
        var attempt = ++_connectAttempt;
        ArmReadySignalTimer(attempt);
        Become(Connecting);
    }

    private void StartConnecting(TConnectCommand command, IActorRef replyTo)
    {
        _healthDetail = _channelDisplayName + " gateway connecting.";
        ResetIdentityState();
        _cleanReconnectEmitted = false;
        _readyEnteredAt = null;

        // Normalize Nobody to null: "no pending caller" must have exactly one
        // representation. The auto-retry path connects with ActorRefs.Nobody,
        // and storing it verbatim made every `_pendingConnectReplyTo is null`
        // check misfire — Discord's ready-timeout handler took the
        // caller-driven branch on retries and parked the actor in
        // CleanReconnectRequired with nothing scheduled to ever leave it
        // (the 0.24.0-beta.2 zombie: all traffic silently dropped until
        // restart), and ConnectionRestored never published after an
        // auto-retry recovery.
        _pendingConnectReplyTo = replyTo.IsNobody() ? null : replyTo;

        var attempt = ++_connectAttempt;
        ArmReadySignalTimer(attempt);
        Become(Connecting);
        BeginStart(command, attempt);
    }

    private void ArmReadySignalTimer(long attempt)
    {
        CancelReadySignalTimer();
        if (ReadySignalTimeout is not { } timeout)
            return;

        _readySignalTimer = Context.System.Scheduler.ScheduleTellOnceCancelable(
            timeout,
            Self,
            new ReadySignalTimedOut(attempt, new ChannelConnectException(
                ChannelConnectFailureKind.Transient,
                $"{_channelDisplayName} gateway did not reach ready within {timeout.TotalSeconds:F0} seconds.")),
            ActorRefs.NoSender);
    }

    /// <summary>Cancels any pending ready-signal timeout. The base cancels on every
    /// exit from Connecting; exposed for channel-specific exits (Discord's fatal closes).</summary>
    protected void CancelReadySignalTimer()
    {
        _readySignalTimer?.Cancel();
        _readySignalTimer = null;
    }

    private void HandleReadySignalTimedOut(ReadySignalTimedOut timedOut)
    {
        if (timedOut.Attempt != _connectAttempt)
            return;

        // ONE canonical transient-fail path whether or not an operator ask is
        // pending: fail the ask, publish CleanReconnectRequired, stop the
        // transport, and land in Disconnected with the auto-retry scheduled.
        // (Branching on HasPendingConnect here previously produced a permanent
        // zombie: a behavior with no retry scheduled and the transport left
        // running.)
        RequestCleanReconnect(timedOut.Exception.Message);
    }

    private void BeginStart(TConnectCommand command, long attempt)
    {
        var self = Self;
        Task<object?> startTask;
        try
        {
            startTask = StartTransportAsync(command);
        }
        catch (Exception ex)
        {
            self.Tell(new StartFailed(attempt, ex));
            return;
        }

        startTask.ContinueWith(
            task => self.Tell(task.IsCompletedSuccessfully
                ? new StartSucceeded(attempt, task.Result)
                : new StartFailed(attempt, UnwrapTaskException(task))),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleStartSucceeded(StartSucceeded started)
    {
        if (started.Attempt != _connectAttempt)
            return;

        OnTransportStartSucceeded(started.StartResult);
    }

    private void HandleStartFailed(StartFailed failed)
    {
        if (failed.Attempt != _connectAttempt)
            return;

        var classified = ClassifyStartFailure(failed.Exception);
        _healthDetail = classified.Message;
        CancelReadySignalTimer();
        FailPendingConnect(classified);

        if (classified.IsFatal)
        {
            Logger.LogError(classified,
                "{Channel} connect hit a fatal failure; auto-reconnect disabled. {Reason}",
                _channelDisplayName, classified.Message);
            _autoReconnect = false;
        }
        else
        {
            ScheduleRetryIfEnabled();
        }

        Become(Disconnected);
    }

    private void StartDisconnecting(IActorRef replyTo, bool preserveAutoReconnect = false)
    {
        CancelRetryTimer();
        if (!preserveAutoReconnect)
            _autoReconnect = false;

        ++_connectAttempt;
        _healthDetail = _channelDisplayName + " gateway disconnecting.";
        _cleanReconnectEmitted = false;
        CancelReadySignalTimer();
        FailPendingConnect(new OperationCanceledException(
            _channelDisplayName + " gateway disconnect requested."));
        Become(Disconnecting);
        BeginStop(replyTo);
    }

    private void BeginStop(IActorRef replyTo)
    {
        var self = Self;
        Task stopTask;
        try
        {
            stopTask = StopTransportAsync();
        }
        catch (Exception ex)
        {
            self.Tell(new StopFailed(replyTo, ex));
            return;
        }

        stopTask.ContinueWith(
            task => self.Tell(task.IsCompletedSuccessfully
                ? new StopSucceeded(replyTo)
                : new StopFailed(replyTo, UnwrapTaskException(task))),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleStopSucceeded(StopSucceeded stopped)
    {
        _healthDetail = _disconnectedDetail;
        ResetIdentityState();
        ScheduleRetryIfEnabled();
        Become(Disconnected);
        if (!stopped.ReplyTo.Equals(ActorRefs.Nobody))
            stopped.ReplyTo.Tell(CurrentSnapshot());
    }

    private void HandleStopFailed(StopFailed failed)
    {
        _healthDetail = failed.Exception.Message;
        ScheduleRetryIfEnabled();
        Become(Disconnected);
        if (!failed.ReplyTo.Equals(ActorRefs.Nobody))
            failed.ReplyTo.Tell(new Status.Failure(failed.Exception));
    }

    /// <summary>
    /// Completes a successful connect: transitions to Ready, replies to a
    /// pending operator connect ask with the snapshot, and publishes
    /// ConnectionRestored.
    /// </summary>
    protected void CompleteConnectToReady()
    {
        TransitionToReady();
        CompletePendingConnect(CurrentSnapshot());

        // Published on EVERY transition to Ready, not just auto-retries: if an
        // operator connect ask timed out client-side but the transport reached
        // Ready afterwards, this event is the channel's only signal to finish
        // its connection setup (channels handle it idempotently).
        Dispatch(
            _channelDisplayName + " connection restored",
            () => PublishConnectionRestoredAsync(CurrentSnapshot()));
    }

    private void TransitionToReady()
    {
        _healthDetail = null;
        _cleanReconnectEmitted = false;
        _readyEnteredAt = _timeProvider.GetUtcNow();
        CancelReadySignalTimer();
        Become(Ready);
    }

    /// <summary>
    /// Forces a teardown of the current transport session: publishes
    /// CleanReconnectRequired (once per cycle), fails any pending connect ask,
    /// then drives a stop with auto-reconnect preserved so a retry follows the
    /// stop — immediately if the previous Ready period was stable, with the
    /// grown backoff otherwise.
    /// </summary>
    protected void RequestCleanReconnect(string reason)
    {
        _healthDetail = reason;
        CancelReadySignalTimer();
        FailPendingConnect(new ChannelConnectException(ChannelConnectFailureKind.Transient, reason));

        if (!_cleanReconnectEmitted)
        {
            _cleanReconnectEmitted = true;
            Logger.LogWarning("Gateway requested clean reconnect: {Reason}", reason);
            Dispatch(
                _channelDisplayName + " clean reconnect",
                () => PublishCleanReconnectRequiredAsync(reason));
        }

        ResetRetryDelayIfStable();
        StartDisconnecting(ActorRefs.Nobody, preserveAutoReconnect: true);
    }

    /// <summary>
    /// Resets the retry backoff to zero only when the connection had been
    /// Ready for at least <see cref="BackoffResetStabilityWindow"/>. The reset
    /// decision is deferred to the failure edge — a connect success alone does
    /// not reset — so a ready→drop flap inside the window keeps the previous
    /// backoff growing (5s → … → 5min) instead of hammering the server with
    /// zero-delay reconnects forever.
    /// </summary>
    private void ResetRetryDelayIfStable()
    {
        if (_readyEnteredAt is { } readyEnteredAt
            && _timeProvider.GetUtcNow() - readyEnteredAt >= BackoffResetStabilityWindow)
        {
            _retryDelay = TimeSpan.Zero;
        }

        _readyEnteredAt = null;
    }

    /// <summary>Schedules the next auto-reconnect attempt with exponential backoff.</summary>
    protected void ScheduleRetryIfEnabled()
    {
        if (!_autoReconnect || _retryConnectCommand is null)
            return;

        CancelRetryTimer();
        _retryTimer = Context.System.Scheduler.ScheduleTellOnceCancelable(
            _retryDelay, Self, RetryConnect.Instance, ActorRefs.NoSender);

        _retryDelay = _retryDelay == TimeSpan.Zero
            ? InitialRetryDelay
            : TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
    }

    private void CancelRetryTimer()
    {
        _retryTimer?.Cancel();
        _retryTimer = null;
    }

    /// <summary>Disables auto-reconnect and cancels any pending retry (fatal closes, operator disconnects).</summary>
    protected void CancelAutoReconnect()
    {
        _autoReconnect = false;
        _retryDelay = TimeSpan.Zero;
        CancelRetryTimer();
    }

    /// <summary>Builds the channel snapshot reflecting current connection, readiness, and health detail.</summary>
    protected TSnapshot CurrentSnapshot()
    {
        var isReady = IsReadyCore();
        var healthDetail = isReady
            ? null
            : _healthDetail ?? (IsTransportConnected ? _notReadyDetail : _disconnectedDetail);

        return CreateSnapshot(IsTransportConnected, isReady, healthDetail);
    }

    /// <summary>
    /// Ready means: the Ready behavior is active AND the bot identity is
    /// resolved AND the transport still reports connected. Re-checked on every
    /// snapshot/ingress because the transport can drop between messages.
    /// </summary>
    protected bool IsReadyCore() => _isReadyBehavior && HasBotIdentity && IsTransportConnected;

    private void CompletePendingConnect(TSnapshot snapshot)
    {
        var replyTo = _pendingConnectReplyTo;
        _pendingConnectReplyTo = null;
        replyTo?.Tell(snapshot);
    }

    /// <summary>Fails any pending operator connect ask with the given exception.</summary>
    protected void FailPendingConnect(Exception exception)
    {
        var replyTo = _pendingConnectReplyTo;
        _pendingConnectReplyTo = null;
        replyTo?.Tell(new Status.Failure(exception));
    }

    /// <summary>
    /// Runs a fire-and-forget event-sink publish; failures are routed back to
    /// the actor as <see cref="DispatchFailed"/> and logged.
    /// </summary>
    protected void Dispatch(string operation, Func<Task> dispatch)
    {
        Task dispatchTask;
        try
        {
            dispatchTask = dispatch();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling {Operation}", operation);
            return;
        }

        if (dispatchTask.IsCompletedSuccessfully)
            return;

        var self = Self;
        dispatchTask.ContinueWith(
            task =>
            {
                if (!task.IsCompletedSuccessfully)
                    self.Tell(new DispatchFailed(operation, UnwrapTaskException(task)));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleDispatchFailed(DispatchFailed failed) =>
        Logger.LogError(failed.Exception, "Error handling {Operation}", failed.Operation);

    protected Exception UnwrapTaskException(Task task)
    {
        if (task.IsCanceled)
            return new TaskCanceledException(task);

        return task.Exception?.GetBaseException()
               ?? new InvalidOperationException(
                   _channelDisplayName + " gateway operation failed without an exception.");
    }

    private void HandleWrongBehaviorMessage(object message, string behaviorName)
    {
        switch (message)
        {
            case IGatewayConnectWork connectWork:
                IgnoreWrongBehaviorConnectWork(connectWork, behaviorName);
                break;
            case IGatewayStopWork stopWork:
                IgnoreWrongBehaviorStopWork(stopWork, behaviorName);
                break;
            case IGatewayInternalMessage:
                Logger.LogDebug(
                    "Ignoring {Channel} gateway internal message {MessageType} while in {State} state.",
                    _channelDisplayName,
                    message.GetType().Name,
                    behaviorName);
                break;
            default:
                Logger.LogWarning(
                    "Ignoring unexpected {Channel} gateway message {MessageType} while in {State} state.",
                    _channelDisplayName,
                    message.GetType().Name,
                    behaviorName);
                break;
        }
    }

    private void IgnoreWrongBehaviorConnectWork(IGatewayConnectWork connectWork, string behaviorName)
    {
        Logger.LogDebug(
            "Ignoring {Channel} gateway connect work {MessageType} for attempt {Attempt} while in {State} state; current attempt is {CurrentAttempt}.",
            _channelDisplayName,
            connectWork.GetType().Name,
            connectWork.Attempt,
            behaviorName,
            _connectAttempt);

        if (_pendingConnectReplyTo is null)
            return;

        if (connectWork is IGatewayConnectFailure failure)
            FailPendingConnect(failure.Exception);
    }

    private void IgnoreWrongBehaviorStopWork(IGatewayStopWork stopWork, string behaviorName)
    {
        Logger.LogDebug(
            "Ignoring {Channel} gateway stop work {MessageType} while in {State} state.",
            _channelDisplayName,
            stopWork.GetType().Name,
            behaviorName);

        if (stopWork is IGatewayStopFailure failure)
        {
            stopWork.ReplyTo.Tell(new Status.Failure(failure.Exception));
            return;
        }

        stopWork.ReplyTo.Tell(CurrentSnapshot());
    }

    /// <summary>
    /// Starts the transport for a connect attempt. The result (e.g. a bot
    /// identity) is delivered to <see cref="OnTransportStartSucceeded"/>;
    /// channels whose start has no result return null.
    /// </summary>
    protected abstract Task<object?> StartTransportAsync(TConnectCommand command);

    /// <summary>Stops the transport (operator disconnects and clean-reconnect teardowns).</summary>
    protected abstract Task StopTransportAsync();

    /// <summary>
    /// Handles a non-stale transport start completion while Connecting.
    /// Channels whose start implies readiness validate and call
    /// <see cref="CompleteConnectToReady"/>; channels with an explicit ready
    /// signal just update <see cref="HealthDetail"/> and keep waiting.
    /// </summary>
    protected abstract void OnTransportStartSucceeded(object? startResult);

    /// <summary>Classifies a transport start failure as fatal or transient.</summary>
    protected abstract ChannelConnectException ClassifyStartFailure(Exception exception);

    /// <summary>Builds the channel snapshot; identity fields come from subclass state.</summary>
    protected abstract TSnapshot CreateSnapshot(bool isConnected, bool isReady, string? healthDetail);

    /// <summary>Clears channel identity state on connect start and stop completion.</summary>
    protected abstract void ResetIdentityState();

    /// <summary>Subscribes transport events in <see cref="PreStart"/> (forward to <see cref="SelfRef"/>).</summary>
    protected abstract void SubscribeTransportEvents();

    /// <summary>Unsubscribes transport events in <see cref="PostStop"/>.</summary>
    protected abstract void UnsubscribeTransportEvents();

    /// <summary>Publishes the CleanReconnectRequired event to the channel's event sink.</summary>
    protected abstract Task PublishCleanReconnectRequiredAsync(string reason);

    /// <summary>Publishes the ConnectionRestored event to the channel's event sink.</summary>
    protected abstract Task PublishConnectionRestoredAsync(TSnapshot snapshot);

    /// <summary>Registers handlers active in every state (transport log messages, etc.).</summary>
    protected abstract void RegisterCommonChannelHandlers();

    /// <summary>Registers drop handlers for ingress arriving while not Ready.</summary>
    protected abstract void RegisterNotReadyIngressHandlers();

    /// <summary>Registers channel transport-event handlers for the Disconnected state.</summary>
    protected abstract void RegisterDisconnectedChannelHandlers();

    /// <summary>Registers channel transport-event handlers for the Connecting state.</summary>
    protected abstract void RegisterConnectingChannelHandlers();

    /// <summary>Registers channel transport-event and ingress handlers for the Ready state.</summary>
    protected abstract void RegisterReadyChannelHandlers();

    /// <summary>Registers channel transport-event handlers for the Disconnecting state.</summary>
    protected abstract void RegisterDisconnectingChannelHandlers();

    /// <summary>Asks the actor for its current <typeparamref name="TSnapshot"/>.</summary>
    public sealed record GetSnapshot
    {
        public static readonly GetSnapshot Instance = new();
    }

    /// <summary>Requests an operator disconnect; replies with the post-stop snapshot.</summary>
    public sealed record Disconnect
    {
        public static readonly Disconnect Instance = new();
    }

    /// <summary>Marker for lifecycle-internal messages so wrong-behavior handling logs them at debug.</summary>
    protected interface IGatewayInternalMessage;

    /// <summary>Marker for transport ingress messages dropped while not Ready.</summary>
    protected interface IGatewayIngressMessage : IGatewayInternalMessage;

    /// <summary>Connect-attempt work stamped so stale attempts can be ignored.</summary>
    protected interface IGatewayConnectWork : IGatewayInternalMessage
    {
        long Attempt { get; }
    }

    protected interface IGatewayConnectFailure : IGatewayConnectWork
    {
        Exception Exception { get; }
    }

    protected interface IGatewayStopWork : IGatewayInternalMessage
    {
        IActorRef ReplyTo { get; }
    }

    protected interface IGatewayStopFailure : IGatewayStopWork
    {
        Exception Exception { get; }
    }

    /// <summary>Reported by <see cref="Dispatch"/> when an event-sink publish faults.</summary>
    protected sealed record DispatchFailed(string Operation, Exception Exception) : IGatewayInternalMessage;

    private sealed record StartSucceeded(long Attempt, object? StartResult) : IGatewayConnectWork;

    private sealed record StartFailed(long Attempt, Exception Exception) : IGatewayConnectFailure;

    /// <summary>
    /// Carries the failure as <see cref="IGatewayConnectFailure"/> so the
    /// wrong-behavior path fails a pending ask if a stale timer ever slips
    /// past the cancel points.
    /// </summary>
    private sealed record ReadySignalTimedOut(long Attempt, Exception Exception) : IGatewayConnectFailure;

    private sealed record StopSucceeded(IActorRef ReplyTo) : IGatewayStopWork;

    private sealed record StopFailed(IActorRef ReplyTo, Exception Exception) : IGatewayStopFailure;

    private sealed record RetryConnect : IGatewayInternalMessage
    {
        public static readonly RetryConnect Instance = new();
    }
}
