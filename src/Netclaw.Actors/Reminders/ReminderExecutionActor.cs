// -----------------------------------------------------------------------
// <copyright file="ReminderExecutionActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Reminders;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Short-lived child actor handling a single reminder execution.
/// Creates a session pipeline, sends reminder instructions, collects output,
/// and reports success/failure to <see cref="ReminderManagerActor"/>.
/// </summary>
internal sealed class ReminderExecutionActor : ReceiveActor
{
    /// <summary>
    /// Backstop timeout for CurrentSession reminders with
    /// <c>DeliveryRequired=true</c> waiting for a
    /// <see cref="ReminderDeliveryResult"/> after session <see cref="CommandAck"/>.
    /// The binding actor normally reports delivery success OR failure
    /// explicitly (so failures redeliver fast); this timeout only fires when
    /// the binding actor never responds at all — e.g. it crashed mid-turn.
    /// It is deliberately generous: firing it early on a slow-but-live turn
    /// would report a false failure and redeliver, duplicating the message.
    /// </summary>
    internal static TimeSpan DeliveryObservedTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// Backstop inactivity ceiling for the Mode A (Channel / own-pipeline) path.
    /// The actor runs its own session pipeline and concludes when it emits
    /// <c>TurnCompleted</c>/<c>Error</c>; if the session wedges and stops
    /// producing output without ever reaching a terminal signal, nothing else
    /// stops this actor, so it would hold the duplicate-execution guard forever
    /// (see #1492). A <see cref="ReceiveTimeout"/> reset by every session output
    /// fires after this much silence and concludes the run as failed, releasing
    /// the guard so the next fire can run. Reset by real output, so it never
    /// preempts a live turn. Mode B (CurrentSession) does NOT arm this — its wait
    /// is bounded separately by <see cref="DeliveryObservedTimeout"/>.
    /// </summary>
    internal static TimeSpan ExecutionStallTimeout = TimeSpan.FromMinutes(20);

    private readonly Guid _executionId;
    private readonly ReminderDefinition _definition;
    private readonly ReminderHistoryStore _historyStore;
    private readonly TimeProvider _timeProvider;
    private readonly ReminderEnvelope<ReminderPayload>? _envelope;
    private readonly ILoggingAdapter _log;
    private readonly DateTimeOffset _dispatchedAt;
    private IReminderClient? _reminderClient;

    private readonly SessionPipelineHandle _handle;
    private readonly ExecutionOutputAccumulator _accumulator;
    private bool _completed;
    private string? _sessionIdValue;
    private HistoryRecord? _pendingHistory;
    private bool _awaitingDeliveryResult;
    private ReminderId? _expectedReminderDeliveryKey;
    private ICancelable? _deliveryTimeoutCancelable;

    private bool RoutesBackToOriginSession => _definition.Delivery.Kind == DeliveryKind.CurrentSession;

    public static Props CreateProps(
        Guid executionId,
        ReminderDefinition definition,
        ISessionPipeline pipeline,
        TimeProvider timeProvider,
        ReminderHistoryStore historyStore,
        ReminderEnvelope<ReminderPayload>? envelope = null) =>
        Props.Create(() => new ReminderExecutionActor(executionId, definition, pipeline, timeProvider, historyStore, envelope));

    public ReminderExecutionActor(
        Guid executionId,
        ReminderDefinition definition,
        ISessionPipeline pipeline,
        TimeProvider timeProvider,
        ReminderHistoryStore historyStore,
        ReminderEnvelope<ReminderPayload>? envelope = null)
    {
        _executionId = executionId;
        _definition = definition;
        _historyStore = historyStore;
        _timeProvider = timeProvider;
        _envelope = envelope;
        _dispatchedAt = timeProvider.GetUtcNow();
        _log = Context.GetLogger();
        _handle = new SessionPipelineHandle(pipeline, _log, "reminder-exec");

        var notificationToolName = definition.Delivery.GetNotificationToolName();
        _accumulator = new ExecutionOutputAccumulator(
            notificationToolName is not null ? new ToolName(notificationToolName) : new ToolName("__none__"),
            (tool, callId, succeeded) =>
            {
                if (succeeded)
                    _log.Info("ReminderExecution NotifySucceeded: execution_id={0} reminder_id={1} tool={2} call_id={3}",
                        _executionId, _definition.Id, tool, callId);
                else
                    _log.Warning("ReminderExecution NotifyFailed: execution_id={0} reminder_id={1} tool={2} call_id={3}",
                        _executionId, _definition.Id, tool, callId);
            });

        Receive<ExecutionOutput>(HandleOutput);
        Receive<ExecutionStarted>(_ => { });
        Receive<ReminderDeliveryResult>(HandleDeliveryResult);
        Receive<DeliveryBackstopTimeout>(HandleDeliveryBackstopTimeout);
        Receive<ReceiveTimeout>(_ => HandleExecutionStall());
    }

    protected override void PreStart()
    {
        _log.Info(
            $"ReminderExecution Dispatched: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} schedule_type={_definition.Schedule.Type} dispatched_at={_dispatchedAt} delivery_kind={_definition.Delivery.Kind}");

        if (RoutesBackToOriginSession)
        {
            _reminderClient = ReminderClientExtension.Get(Context.System)
                .CreateClient(new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId));
        }

        Self.Tell(new ExecutionStarted());
        RunTask(RoutesBackToOriginSession ? InitializeCurrentSessionAsync : InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var sessionId = !string.IsNullOrWhiteSpace(_definition.Delivery.SessionId)
                ? new SessionId(_definition.Delivery.SessionId)
                : new SessionId($"reminder/{_definition.Id}/{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}");

            _sessionIdValue = sessionId.Value;
            var audience = _definition.Audience;
            var boundary = GetPersistedBoundaryOrThrow();

            _log.Info(
                $"ReminderExecution Initialized: execution_id={_executionId} reminder_id={_definition.Id} session_id={sessionId.Value} audience={audience} source=stored-definition");

            var self = Self;
            var inputQueue = await _handle.InitializeWithQueueAsync(
                Context,
                sessionId,
                new SessionPipelineOptions
                {
                    ChannelType = Channels.ChannelType.Reminder,
                    Filter = OutputFilter.TextStreaming | OutputFilter.ToolCalls
                },
                output => self.Tell(new ExecutionOutput(output)));

            var prompt = BuildPrompt(_definition);

            await inputQueue.OfferAsync(new ChannelInput
            {
                SenderId = new Protocol.SenderId("reminder-system"),
                ChannelId = _definition.Delivery.Address,
                Audience = audience,
                Boundary = boundary,
                Principal = PrincipalClassification.VerifiedAutomation,
                Provenance = new SourceProvenance(
                    TransportAuthenticity.LocalProcess,
                    PayloadTaint.Trusted)
                {
                    SourceKind = new SourceKind("reminder")
                },
                Contents = [new TextContent(prompt)],
                ReceivedAt = _timeProvider.GetUtcNow(),
                RequestedDeliveryTarget = _definition.Delivery.Kind == DeliveryKind.Channel
                    ? ResolveChannelDeliveryTarget(_definition)
                    : null
            });

            inputQueue.Complete();

            // Arm the Mode A stall backstop: the pipeline now streams output to
            // this actor, each ExecutionOutput resets the ReceiveTimeout, and a
            // terminal TurnCompleted/Error stops us first. If the session wedges
            // and goes silent without a terminal signal, this fires and releases
            // the duplicate-execution guard instead of hanging forever (#1492).
            Context.SetReceiveTimeout(ExecutionStallTimeout);
        }
        catch (Exception ex)
        {
            LogFullException(ex, "ReminderExecution InitializationFailed");
            ReportAndStop(false, ex.Message);
        }
    }

    /// <summary>
    /// Mode B path: dispatches the reminder as a <c>DeliverTrustedSessionTurn</c>
    /// to the originating channel's gateway and calls
    /// <c>IReminderClient.AckAsync(envelope)</c> exactly once once the
    /// target session has acknowledged receipt via the
    /// <c>MessageSource.AckTarget</c>-propagated <c>CommandAck</c>. When
    /// <c>DeliveryRequired</c> is true, ack is further gated on a
    /// <see cref="ReminderDeliveryResult"/> reporting an actual successful
    /// post (the binding actor tells it directly via
    /// <c>MessageSource.DeliveryObserver</c>). On <c>CommandNack</c>, a
    /// delivery failure, the backstop timeout, or any exception,
    /// <c>AckAsync</c> is NOT called and Akka.Reminders redelivers per its
    /// built-in policy.
    /// </summary>
    private async Task InitializeCurrentSessionAsync()
    {
        try
        {
            var sessionId = new SessionId(_definition.Delivery.SessionId!);
            _sessionIdValue = sessionId.Value;
            var originChannelType = _definition.Delivery.OriginChannelType!.Value;

            var audience = _definition.Audience;
            var boundary = GetPersistedBoundaryOrThrow();

            // Dedup key must be STABLE across Akka.Reminders redeliveries of the
            // same fire, or the target session can't recognize a redelivery and
            // re-runs it (duplicate delivery). The envelope's scheduled fire time
            // (DueTimeUtc) is identical on every redelivery; _dispatchedAt is
            // captured fresh per execution actor and drifts, defeating the dedup.
            // Deferred re-runs carry no envelope and are never redelivered, so the
            // dispatch time is a fine fallback there.
            var fireTimeMs = (_envelope?.DueTimeUtc ?? _dispatchedAt).ToUnixTimeMilliseconds();
            var reminderDeliveryKey = $"{_definition.Id}:{fireTimeMs}";

            _log.Info(
                "ReminderExecution CurrentSession Initialized: execution_id={ExecutionId} reminder_id={ReminderId} session_id={SessionId} origin={Origin} audience={Audience}",
                _executionId, _definition.Id, sessionId.Value, originChannelType, audience);

            var source = new MessageSource
            {
                ChannelType = originChannelType,
                SenderId = new Protocol.SenderId("reminder-system"),
                MessageId = reminderDeliveryKey,
                TurnId = new Protocol.TurnId(reminderDeliveryKey),
                Audience = audience,
                Boundary = boundary,
                Principal = PrincipalClassification.VerifiedAutomation,
                Provenance = new SourceProvenance(
                    TransportAuthenticity.LocalProcess,
                    PayloadTaint.Trusted)
                {
                    SourceKind = new SourceKind("reminder")
                },
                ReceivedAt = _dispatchedAt,
                ReminderId = new ReminderId(reminderDeliveryKey),
                // Only reminders that gate on delivery need a confirmation
                // channel. The binding actor tells this ref a
                // ReminderDeliveryResult on turn completion; leaving it null
                // for non-required reminders avoids a dead-letter when this
                // actor has already acked and stopped.
                DeliveryObserver = _definition.DeliveryRequired ? Self : null
            };

            var gateway = ResolveGatewayFor(originChannelType);
            if (gateway is null)
            {
                ReportAndStop(false, $"Mode B unsupported origin channel type: {originChannelType}");
                return;
            }

            var deliverMsg = new DeliverTrustedSessionTurn(sessionId, BuildPrompt(_definition), source);

            try
            {
                var ack = await gateway.Ask<object>(deliverMsg, ReminderSettings.DefaultAckTimeout);
                switch (ack)
                {
                    case CommandAck:
                        _log.Info(
                            "reminder_current_session_dispatch_acked execution_id={ExecutionId} reminder_id={ReminderId} session_id={SessionId}",
                            _executionId, _definition.Id, sessionId.Value);

                        if (_definition.DeliveryRequired)
                        {
                            // Do NOT await the delivery result here: this runs
                            // inside RunTask, which suspends the mailbox until
                            // the task completes — the actor could not process
                            // the ReminderDeliveryResult message it is waiting
                            // for. Arm state + a backstop timer and return; the
                            // result (or timeout) is handled as a normal message.
                            BeginAwaitingDeliveryResult(new ReminderId(reminderDeliveryKey));
                            break;
                        }

                        await TryAckEnvelopeAsync();
                        ReportAndStop(true);
                        break;

                    case CommandNack nack:
                        _log.Warning(
                            "reminder_current_session_nack execution_id={ExecutionId} reminder_id={ReminderId} session_id={SessionId} reason={Reason}",
                            _executionId, _definition.Id, sessionId.Value, nack.Reason);
                        ReportAndStop(false, $"Session rejected reminder delivery: {nack.Reason}");
                        break;

                    default:
                        _log.Warning(
                            "reminder_current_session_unexpected_reply execution_id={ExecutionId} reminder_id={ReminderId} reply_type={ReplyType}",
                            _executionId, _definition.Id, ack?.GetType().FullName ?? "null");
                        ReportAndStop(false, "Unexpected reply from channel gateway");
                        break;
                }
            }
            catch (AskTimeoutException)
            {
                _log.Warning(
                    "reminder_current_session_timeout execution_id={ExecutionId} reminder_id={ReminderId} session_id={SessionId} timeout={Timeout}",
                    _executionId, _definition.Id, sessionId.Value, ReminderSettings.DefaultAckTimeout);
                ReportAndStop(false, "Timed out waiting for session ack");
            }
        }
        catch (Exception ex)
        {
            LogFullException(ex, "ReminderExecution CurrentSession InitializationFailed");
            ReportAndStop(false, ex.Message);
        }
    }

    /// <summary>
    /// Enters the message-driven "waiting for delivery confirmation" state and
    /// schedules a backstop timeout. Runs at the tail of the dispatch
    /// <c>RunTask</c>; once that task returns, the actor resumes normal mailbox
    /// processing and can handle the <see cref="ReminderDeliveryResult"/> the
    /// binding actor tells it (or the <see cref="DeliveryBackstopTimeout"/>).
    /// </summary>
    private void BeginAwaitingDeliveryResult(ReminderId reminderDeliveryKey)
    {
        _awaitingDeliveryResult = true;
        _expectedReminderDeliveryKey = reminderDeliveryKey;
        _deliveryTimeoutCancelable = Context.System.Scheduler.ScheduleTellOnceCancelable(
            DeliveryObservedTimeout,
            Self,
            new DeliveryBackstopTimeout(reminderDeliveryKey),
            Self);
    }

    private void HandleDeliveryResult(ReminderDeliveryResult result)
    {
        if (!_awaitingDeliveryResult || _expectedReminderDeliveryKey is not { } expectedKey)
            return;

        // Correlate on the delivery key alone. The result was told point-to-
        // point to this actor's own Self (via MessageSource.DeliveryObserver),
        // and the key is unique per execution — matching the reported
        // ChannelType too would only fail closed (e.g. a cold SignalR actor
        // reports its default Tui rather than the origin SignalR), silently
        // dropping a valid result and stalling on the backstop.
        if (result.ReminderDeliveryKey != expectedKey)
            return;

        _awaitingDeliveryResult = false;
        _deliveryTimeoutCancelable?.Cancel();
        _deliveryTimeoutCancelable = null;

        if (result.Delivered)
        {
            _log.Info(
                "reminder_delivery_observed execution_id={ExecutionId} reminder_id={ReminderId} key={ReminderDeliveryKey} channel={ChannelType}",
                _executionId, _definition.Id, result.ReminderDeliveryKey, result.ChannelType);
            RunTask(async () =>
            {
                try
                {
                    await TryAckEnvelopeAsync();
                    ReportAndStop(true);
                }
                catch (Exception ex)
                {
                    // Never let an ack fault escalate to a supervisor restart:
                    // that would re-run PreStart and re-post the reminder turn
                    // in a loop. Report failure (no ack) and stop instead.
                    LogFullException(ex, "ReminderExecution AckFailed");
                    ReportAndStop(false, ex.Message);
                }
            });
        }
        else
        {
            _log.Warning(
                "reminder_delivery_failed execution_id={ExecutionId} reminder_id={ReminderId} key={ReminderDeliveryKey} channel={ChannelType} reason={Reason}",
                _executionId, _definition.Id, result.ReminderDeliveryKey, result.ChannelType, result.FailureReason);
            ReportAndStop(false, result.FailureReason ?? "channel reported delivery failure");
        }
    }

    private void HandleDeliveryBackstopTimeout(DeliveryBackstopTimeout msg)
    {
        if (!_awaitingDeliveryResult || _expectedReminderDeliveryKey is not { } expectedKey)
            return;

        if (msg.ReminderDeliveryKey != expectedKey)
            return;

        _awaitingDeliveryResult = false;
        _deliveryTimeoutCancelable = null;
        _log.Warning(
            "reminder_delivery_observation_timeout execution_id={ExecutionId} reminder_id={ReminderId} key={ReminderDeliveryKey} timeout={Timeout}",
            _executionId, _definition.Id, msg.ReminderDeliveryKey, DeliveryObservedTimeout);
        ReportAndStop(false, $"delivery not observed within {DeliveryObservedTimeout}");
    }

    private async Task TryAckEnvelopeAsync()
    {
        // No envelope to ack when the reminder was re-run from the deferred
        // queue: that path already acked-and-dropped the envelope eagerly
        // (ReminderManagerActor concurrency gate). Acking null would throw.
        if (_envelope is null)
            return;

        var ackResponse = await _reminderClient!.AckAsync(_envelope);
        if (ackResponse.ResponseCode != ReminderAckResponseCode.Success)
        {
            _log.Warning(
                "reminder_ack_non_success execution_id={ExecutionId} reminder_id={ReminderId} response={ResponseCode} message={Message}",
                _executionId, _definition.Id, ackResponse.ResponseCode, ackResponse.Message);
        }
    }

    private IActorRef? ResolveGatewayFor(ChannelType originChannelType)
    {
        var registry = ActorRegistry.For(Context.System);
        return originChannelType switch
        {
            ChannelType.Slack => registry.TryGet<SlackGatewayActorKey>(out var slack) ? slack : null,
            ChannelType.Discord => registry.TryGet<DiscordGatewayActorKey>(out var discord) ? discord : null,
            ChannelType.Mattermost => registry.TryGet<MattermostGatewayActorKey>(out var mattermost) ? mattermost : null,
            ChannelType.Tui => registry.TryGet<SignalRGatewayActorKey>(out var signalr) ? signalr : null,
            ChannelType.SignalR => registry.TryGet<SignalRGatewayActorKey>(out var signalr2) ? signalr2 : null,
            _ => null
        };
    }

    private TrustBoundary GetPersistedBoundaryOrThrow()
    {
        if (!SecurityPolicyDefaults.TryNormalizeBoundary(_definition.Boundary.Value, out var normalizedBoundary))
        {
            throw new InvalidOperationException(
                $"Reminder '{_definition.Id}' has invalid persisted trust boundary '{_definition.Boundary}'.");
        }

        return normalizedBoundary;
    }

    private static string BuildPrompt(ReminderDefinition definition)
    {
        var deliverySection = definition.Delivery.Kind switch
        {
            DeliveryKind.CurrentSession => string.IsNullOrWhiteSpace(definition.DeliveryInstructions)
                ? ""
                : $"\n\nDelivery guidance:\n{definition.DeliveryInstructions}",
            DeliveryKind.Channel => BuildChannelDeliveryGuidance(definition) +
                (string.IsNullOrWhiteSpace(definition.DeliveryInstructions) ? "" : $"\n{definition.DeliveryInstructions}"),
            DeliveryKind.None => "",
            _ => throw new ArgumentOutOfRangeException(nameof(definition.Delivery.Kind), definition.Delivery.Kind, "Unexpected DeliveryKind")
        };

        var completionGuidance = definition.Schedule.Type is ReminderScheduleType.Interval or ReminderScheduleType.Cron
            ? $"\n\nThis is a recurring reminder (ID: {definition.Id}). If you determine that its purpose " +
              "has been permanently fulfilled (e.g., the PR merged, the deploy completed, the issue was " +
              "resolved), call cancel_reminder to stop future executions."
            : "";

        return $"{definition.Instructions}{deliverySection}{completionGuidance}";
    }

    private static string BuildChannelDeliveryGuidance(ReminderDefinition definition)
    {
        var target = ResolveChannelDeliveryTarget(definition);
        if (target is not null)
        {
            return "\n\nPost the result using send_channel_message with " +
                   $"channel_key='{target.ChannelKey}', destination.channel_key='{target.ChannelKey}', " +
                   $"destination.kind='{target.DestinationKind}', destination.id='{target.DestinationId}', and text set to the result.";
        }

        throw new InvalidOperationException(
            $"Reminder '{definition.Id}' has channel delivery but could not resolve a delivery target. " +
            "Transport and address may be missing or invalid.");
    }

    private static ChannelDeliveryTargetInfo? ResolveChannelDeliveryTarget(ReminderDefinition definition)
    {
        if (definition.Delivery.Target is not null)
            return definition.Delivery.Target;

        if (definition.Delivery.Kind != DeliveryKind.Channel)
            return null;

        var transport = definition.Delivery.Transport?.Trim().ToLowerInvariant();
        var address = definition.Delivery.Address?.Trim();
        if (string.IsNullOrWhiteSpace(transport) || string.IsNullOrWhiteSpace(address))
            return null;

        var destinationKind = "destination";
        var destinationId = address;

        if (string.Equals(transport, "slack", StringComparison.OrdinalIgnoreCase)
            && address is { Length: > 0 }
            && (address.StartsWith("U", StringComparison.Ordinal) || address.StartsWith("W", StringComparison.Ordinal)))
        {
            destinationKind = "direct_message";
        }
        else if (string.Equals(transport, "mattermost", StringComparison.OrdinalIgnoreCase)
                 && address is { Length: > 0 })
        {
            if (address.StartsWith('@'))
            {
                destinationKind = "direct_message";
                destinationId = address[1..];
            }
            else if (address.StartsWith("channel:", StringComparison.OrdinalIgnoreCase))
            {
                destinationId = address[8..];
            }
        }

        return new ChannelDeliveryTargetInfo(
            transport,
            destinationKind,
            destinationId,
            address);
    }

    private void HandleOutput(ExecutionOutput wrapper)
    {
        var action = _accumulator.ProcessOutput(wrapper.Output);
        switch (action)
        {
            case OutputAction.TurnCompleted:
            {
                var result = _accumulator.GetAccumulatedText();
                var notifyFailureMessage = _accumulator.BuildNotifyFailureMessage(
                    _definition.Delivery.Kind == DeliveryKind.Channel,
                    _definition.DeliveryRequired);
                var success = notifyFailureMessage is null;
                _log.Info(
                    $"ReminderExecution Completed: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} success={success} output_length={result.Length} notify_attempted={_accumulator.NotifyAttempted} notify_failed={_accumulator.NotifyFailed} dispatched_at={_dispatchedAt} completed_at={_timeProvider.GetUtcNow()}");

                ReportAndStop(success, notifyFailureMessage);
                break;
            }

            case OutputAction.Error:
            {
                var completedAt = _timeProvider.GetUtcNow();
                var failedMsg = $"ReminderExecution Failed: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} success=false error_type={_accumulator.LastErrorCategory} error_message={_accumulator.LastErrorMessage} dispatched_at={_dispatchedAt} completed_at={completedAt}";
                if (_accumulator.LastErrorCause is not null)
                    _log.Error(_accumulator.LastErrorCause, "{0}\n{1}", failedMsg, _accumulator.LastErrorCause.ToString());
                else
                    _log.Warning("{0}", failedMsg);
                ReportAndStop(false, _accumulator.LastErrorMessage);
                break;
            }
        }
    }

    private void HandleExecutionStall()
    {
        if (_completed)
            return;

        var elapsed = _timeProvider.GetUtcNow() - _dispatchedAt;
        _log.Warning(
            "ReminderExecution Stalled: execution_id={0} reminder_id={1} title={2} no session output for {3} (elapsed={4}); concluding as failed to release the execution guard.",
            _executionId, _definition.Id, _definition.Title, ExecutionStallTimeout, elapsed);
        ReportAndStop(false, $"Reminder execution stalled: no session output for {ExecutionStallTimeout}.");
    }

    private void ReportAndStop(bool success, string? errorMessage = null)
    {
        if (_completed)
            return;

        _completed = true;

        // Disarm the Mode A stall backstop so it cannot fire during the drain.
        Context.SetReceiveTimeout(null);

        var durationMs = (long)(_timeProvider.GetUtcNow() - _dispatchedAt).TotalMilliseconds;
        _pendingHistory = new HistoryRecord(
            FiredAt: _dispatchedAt,
            Success: success,
            DurationMs: durationMs,
            SessionId: _sessionIdValue ?? $"reminder/{_definition.Id}/unknown",
            ErrorMessage: errorMessage);

        if (!success)
        {
            _log.Warning(
                $"ReminderExecution ReportFailed: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} success=false error_message={errorMessage}");
        }

        Context.Parent.Tell(new ReminderExecutionCompleted(
            _executionId,
            _definition.Id,
            success,
            errorMessage));

        // Drain stream stages before stopping so they complete gracefully
        // rather than being abruptly terminated as actor children.
        RunTask(async () =>
        {
            await _handle.DrainAsync();
            Context.Stop(Self);
        });
    }

    protected override void PostStop()
    {
        _deliveryTimeoutCancelable?.Cancel();
        _deliveryTimeoutCancelable = null;

        if (_pendingHistory is not null)
        {
            try
            {
                _historyStore.AppendAsync(_definition.Id, _pendingHistory)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to write execution history for reminder '{0}'", _definition.Id);
            }
        }

        try
        {
            _handle.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to dispose reminder execution resources for '{0}'", _definition.Title);
        }
    }

    private void LogFullException(Exception ex, string phase)
    {
        var completedAt = _timeProvider.GetUtcNow();
        var msg = $"{phase}: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} success=false dispatched_at={_dispatchedAt} completed_at={completedAt}\n{ex}";
        _log.Error(ex, "{0}", msg);
    }

    private sealed record ExecutionStarted : INoSerializationVerificationNeeded;
    private sealed record ExecutionOutput(SessionOutput Output) : INoSerializationVerificationNeeded;

    /// <summary>
    /// Self-scheduled backstop fired when no <see cref="ReminderDeliveryResult"/>
    /// arrives within <see cref="DeliveryObservedTimeout"/> (e.g. the binding
    /// actor crashed mid-turn).
    /// </summary>
    private sealed record DeliveryBackstopTimeout(ReminderId ReminderDeliveryKey) : INoSerializationVerificationNeeded;
}
