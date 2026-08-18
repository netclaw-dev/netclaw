// -----------------------------------------------------------------------
// <copyright file="SignalRSessionActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Microsoft.AspNetCore.SignalR;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Per-session binding actor for SignalR connections.
/// Uses <see cref="SessionPipelineHandle"/> for pipeline lifecycle so that
/// all Akka.Streams stage actors become children and are automatically stopped
/// when this actor stops.
/// </summary>
internal sealed class SignalRSessionActor : ReceiveActor, IWithUnboundedStash, IWithTimers
{
    private readonly SessionId _sessionId;
    private readonly IHubContext<SessionHub, ISessionHubClient> _hubContext;
    private readonly ILoggingAdapter _log;

    private readonly SessionPipelineHandle _handle;
    private SignalRConnectionId _currentConnectionId;
    private Actors.Channels.ChannelType _channelType = Actors.Channels.ChannelType.Tui;
    private bool _deliveredThisTurn;
    private bool _turnInFlight;
    // Each key connects an accepted reminder turn to its delivery observer.
    // The actor removes the entry after turn completion or enqueue failure.
    private readonly Dictionary<ReminderId, IActorRef> _reminderDeliveryObservers = new();

    private static readonly TimeSpan PipelineInitTimeout = TimeSpan.FromSeconds(15);
    private static readonly object ReinitializeTimerKey = new();

    public IStash Stash { get; set; } = null!;
    public ITimerScheduler Timers { get; set; } = null!;

    public SignalRSessionActor(
        string entityId,
        ISessionPipeline pipeline,
        IHubContext<SessionHub, ISessionHubClient> hubContext)
    {
        _sessionId = new SessionId(entityId);
        _hubContext = hubContext;
        _log = Context.GetLogger()
            .WithContext("Adapter", "signalr")
            .WithContext(NetclawLogProperties.SessionId, _sessionId.Value);
        _handle = new SessionPipelineHandle(pipeline, _log, "signalr");

        Initializing();
    }

    public static Props CreateProps(string entityId, ISessionPipeline pipeline,
        IHubContext<SessionHub, ISessionHubClient> hubContext)
        => Props.Create(() => new SignalRSessionActor(entityId, pipeline, hubContext));

    private SessionPipelineOptions BuildOptions() => new()
    {
        ChannelType = _channelType
    };

    private void Initializing()
    {
        ReceiveAsync<StartSignalRSession>(async msg =>
        {
            _channelType = msg.ChannelType;
            _currentConnectionId = msg.ConnectionId;

            try
            {
                await EnsureInitializedAsync();
                Become(Active);
                Stash.UnstashAll();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to initialize SignalR session pipeline; stopping actor");
                Context.Stop(Self);
            }
        });

        ReceiveAsync<DeliverTrustedSessionTurn>(async msg =>
        {
            try
            {
                await EnsureInitializedAsync();
                Become(Active);
                Self.Forward(msg);
                Stash.UnstashAll();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to initialize SignalR session pipeline for Mode B reminder; stopping actor");
                Sender.Tell(CommandDeferred.For(_sessionId, $"SignalR pipeline initialization failed: {ex.Message}"));
                Context.Stop(Self);
            }
        });

        ReceiveAny(_ => Stash.Stash());
    }

    private void Active()
    {
        Receive<AttachSignalRConnection>(msg =>
        {
            _currentConnectionId = msg.ConnectionId;
            _log.Debug("Connection {ConnectionId} attached to session", msg.ConnectionId.Value);
        });

        ReceiveAsync<EnqueueSignalRInput>(async msg =>
        {
            var writer = _handle.InputQueue;
            if (writer is null)
            {
                _log.Warning("Input queue not initialized; dropping message for session {SessionId}", _sessionId.Value);
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await writer.WriteAsync(msg.Input, cts.Token);
                _turnInFlight = true;
            }
            catch (OperationCanceledException)
            {
                _log.Warning("Timed out writing to input queue for session {SessionId}", _sessionId.Value);
            }
            catch (ChannelClosedException)
            {
                _log.Warning("Input queue closed for session {SessionId}; reinitializing", _sessionId.Value);
                Self.Tell(new ReinitializePipeline("input queue closed"));
            }
        });

        ReceiveAsync<OutputReceived>(HandleOutputReceivedAsync);

        Receive<OutputStreamTerminated>(msg =>
        {
            if (msg.Generation != _handle.Generation)
                return;

            var reason = msg.Cause is null
                ? "completed"
                : $"faulted: {msg.Cause.Message}";

            _log.Warning("SignalR output stream terminated ({Reason}); reinitializing pipeline", reason);
            Self.Tell(new ReinitializePipeline(reason));
        });

        ReceiveAsync<ReinitializePipeline>(async msg =>
        {
            _deliveredThisTurn = false;
            _turnInFlight = false;
            // A reinit aborts any in-flight reminder turn before its
            // TurnCompleted. Report those as not-delivered now so the
            // execution actor redelivers immediately instead of stalling
            // until the backstop timeout.
            FailPendingReminderDeliveries($"SignalR pipeline reinitialized: {msg.Reason}");
            await _handle.ReinitializeAsync(
                msg.Reason,
                () => Timers.StartSingleTimer(
                    ReinitializeTimerKey,
                    new ReinitializePipeline("retry after failed reinit"),
                    TimeSpan.FromSeconds(2)));
        });

        Receive<ShutdownSignalRSession>(_ =>
        {
            _log.Debug("Shutdown requested for session {SessionId}", _sessionId.Value);
            RunTask(async () =>
            {
                await _handle.DrainAsync();
                Context.Stop(Self);
            });
        });

        ReceiveAsync<DeliverTrustedSessionTurn>(async msg =>
        {
            var ackTarget = Sender;

            if (_turnInFlight)
            {
                _log.Info("Deferring CurrentSession reminder because the session has an active turn");
                ackTarget.Tell(CommandDeferred.For(_sessionId, "The target session has an active turn."));
                return;
            }

            var writer = _handle.InputQueue;
            if (writer is null)
            {
                _log.Warning(
                    "SignalR input queue not initialized; deferring CurrentSession reminder for {SessionId}",
                    _sessionId.Value);
                ackTarget.Tell(CommandDeferred.For(_sessionId, "SignalR pipeline is not initialized."));
                return;
            }

            var input = new ChannelInput
            {
                SenderId = msg.Source.SenderId,
                ChannelId = msg.Source.ChannelId,
                MessageId = msg.Source.MessageId,
                Audience = msg.Source.Audience,
                Boundary = msg.Source.Boundary,
                Principal = msg.Source.Principal,
                Provenance = msg.Source.Provenance,
                Contents = [new Microsoft.Extensions.AI.TextContent(msg.Content)],
                ReceivedAt = msg.Source.ReceivedAt,
                DefaultDeliveryTarget = BuildDefaultDeliveryTarget(),
                RequestedDeliveryTarget = msg.Source.RequestedDeliveryTarget,
                ReminderId = msg.Source.ReminderId,
                AckTarget = ackTarget
            };

            // Only delivery-gated reminders carry a DeliveryObserver.
            // The per-fire key connects the completed turn to the correct observer.
            if (msg.Source.DeliveryObserver is { } deliveryObserver
                && msg.Source.ReminderId is { } reminderKey
                && !string.IsNullOrWhiteSpace(reminderKey.Value))
                _reminderDeliveryObservers[reminderKey] = deliveryObserver;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await writer.WriteAsync(input, cts.Token);
                _turnInFlight = true;
                _log.Debug(
                    "reminder_mode_b_dispatch session={Session} reminder={Reminder}",
                    _sessionId.Value, msg.Source.ReminderId);
            }
            catch (OperationCanceledException)
            {
                _log.Warning("Timed out enqueueing Mode B reminder for session {SessionId}", _sessionId.Value);
                RemoveReminderDeliveryObserver(msg.Source.ReminderId);
                ackTarget.Tell(CommandDeferred.For(_sessionId, "The SignalR pipeline enqueue timed out."));
            }
            catch (ChannelClosedException)
            {
                _log.Warning("SignalR input queue closed; deferring CurrentSession reminder for {SessionId}", _sessionId.Value);
                RemoveReminderDeliveryObserver(msg.Source.ReminderId);
                ackTarget.Tell(CommandDeferred.For(_sessionId, "The SignalR input queue is closed."));
                Self.Tell(new ReinitializePipeline("input queue closed during Mode B delivery"));
            }
        });
    }

    private async Task EnsureInitializedAsync()
    {
        if (_handle.IsInitialized)
            return;

        var self = Self;
        using var initCts = new CancellationTokenSource(PipelineInitTimeout);
        await _handle.InitializeWithChannelAsync(
            Context,
            _sessionId,
            BuildOptions(),
            output => self.Tell(new OutputReceived(output)),
            (gen, cause) => self.Tell(new OutputStreamTerminated(gen, cause)),
            initCts.Token);
    }

    private async Task HandleOutputReceivedAsync(OutputReceived msg)
    {
        try
        {
            if (_currentConnectionId == default)
            {
                // No connected client → nothing was delivered. Report the
                // outcome (false) so a DeliveryRequired reminder redelivers
                // rather than waiting out the backstop timeout.
                if (msg.Output is TurnCompleted noConnTurn)
                {
                    ReportReminderDeliveryResult(noConnTurn, delivered: false);
                    _deliveredThisTurn = false;
                    _turnInFlight = false;
                }
                return;
            }

            var dto = SessionOutputDtoMapper.ToDto(msg.Output);
            await _hubContext.Clients.Client(_currentConnectionId.Value).ReceiveOutput(dto);

            if (msg.Output is TextOutput or ErrorOutput or FileOutput)
            {
                _deliveredThisTurn = true;
                return;
            }

            if (msg.Output is TurnCompleted completed)
            {
                ReportReminderDeliveryResult(completed, delivered: _deliveredThisTurn);
                _deliveredThisTurn = false;
                _turnInFlight = false;
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex,
                "Failed to deliver output to connection {ConnectionId}", _currentConnectionId.Value);

            // The hub send threw → the reply did not reach the client. Report
            // failure so the reminder redelivers instead of stalling.
            if (msg.Output is TurnCompleted failedTurn)
            {
                ReportReminderDeliveryResult(failedTurn, delivered: false);
                _deliveredThisTurn = false;
                _turnInFlight = false;
            }
        }
    }

    /// <summary>
    /// Tells the dispatching reminder execution actor (if this turn was a
    /// reminder delivery) whether the assistant reply reached the client,
    /// then removes that observer.
    /// </summary>
    private void ReportReminderDeliveryResult(TurnCompleted completed, bool delivered)
    {
        if (completed.SourceReminderId is { } sourceReminderKey
            && !string.IsNullOrWhiteSpace(sourceReminderKey.Value)
            && _reminderDeliveryObservers.Remove(sourceReminderKey, out var observer))
        {
            observer.Tell(new ReminderDeliveryResult(
                sourceReminderKey,
                _channelType,
                Delivered: delivered,
                FailureReason: delivered ? null : "SignalR client did not receive the reply",
                ObservedAtMs: completed.TimestampMs));
        }
    }

    private void RemoveReminderDeliveryObserver(ReminderId? reminderKey)
    {
        if (reminderKey is { } key)
            _reminderDeliveryObservers.Remove(key);
    }

    /// <summary>
    /// Tells every in-flight reminder observer that delivery did not happen,
    /// then clears them. Called when a turn can no longer reach TurnCompleted
    /// (e.g. pipeline reinit), so the execution actor fails fast and redelivers
    /// rather than waiting out its backstop timeout.
    /// </summary>
    private void FailPendingReminderDeliveries(string reason)
    {
        if (_reminderDeliveryObservers.Count == 0)
            return;

        foreach (var (key, observer) in _reminderDeliveryObservers)
            observer.Tell(new ReminderDeliveryResult(key, _channelType, Delivered: false, FailureReason: reason));

        _reminderDeliveryObservers.Clear();
    }

    protected override void PostStop()
    {
        _handle.Dispose();
        base.PostStop();
    }

    private ChannelDeliveryTargetInfo BuildDefaultDeliveryTarget()
        => new(
            (_channelType == Actors.Channels.ChannelType.SignalR
                ? Actors.Channels.ChannelType.Tui
                : _channelType).ToWireValue(),
            "local_session",
            _sessionId.Value,
            _sessionId.Value);

    // ─── Message protocol ───────────────────────────────────────────────────

    private sealed record OutputReceived(SessionOutput Output);
    private sealed record OutputStreamTerminated(int Generation, Exception? Cause);
    private sealed record ReinitializePipeline(string Reason);
}

// ─── Gateway-routable messages (implement ISignalRSessionMessage) ────────────

/// <summary>Marker for messages routable to a <see cref="SignalRSessionActor"/>.</summary>
internal interface ISignalRSessionMessage
{
    SessionId SessionId { get; }
}

/// <summary>Creates (or re-creates) a SignalR session binding actor.</summary>
internal sealed record StartSignalRSession(
    SessionId SessionId,
    Actors.Channels.ChannelType ChannelType,
    SignalRConnectionId ConnectionId) : ISignalRSessionMessage;

/// <summary>Updates the active connection ID for an existing session.</summary>
internal sealed record AttachSignalRConnection(
    SessionId SessionId,
    SignalRConnectionId ConnectionId) : ISignalRSessionMessage;

/// <summary>Delivers a user message to the session's input pipeline.</summary>
internal sealed record EnqueueSignalRInput(
    SessionId SessionId,
    ChannelInput Input) : ISignalRSessionMessage;

/// <summary>Requests graceful actor shutdown and stream cleanup.</summary>
internal sealed record ShutdownSignalRSession(SessionId SessionId) : ISignalRSessionMessage;

/// <summary>
/// Routes messages to <see cref="SignalRSessionActor"/> children by session ID.
/// </summary>
internal sealed class SignalRMessageExtractor : Akka.Cluster.Sharding.HashCodeMessageExtractor
{
    public SignalRMessageExtractor() : base(40) { }

    public override string? EntityId(object message) => message switch
    {
        ISignalRSessionMessage msg => msg.SessionId.Value,
        Actors.Protocol.IWithSessionId wid => wid.SessionId.Value,
        _ => null
    };
}
