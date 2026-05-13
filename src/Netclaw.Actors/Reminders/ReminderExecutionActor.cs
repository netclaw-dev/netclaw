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

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Short-lived child actor handling a single reminder execution.
/// Creates a session pipeline, sends reminder instructions, collects output,
/// and reports success/failure to <see cref="ReminderManagerActor"/>.
/// </summary>
internal sealed class ReminderExecutionActor : ReceiveActor
{
    /// <summary>
    /// How long CurrentSession reminders with <c>DeliveryRequired=true</c>
    /// wait for outbound delivery observation after session <see cref="CommandAck"/>.
    /// </summary>
    internal static TimeSpan DeliveryObservedTimeout = TimeSpan.FromHours(1);

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
    private TaskCompletionSource<ReminderDeliveryObserved>? _deliveryObservedTcs;
    private string? _expectedReminderDeliveryKey;
    private ChannelType? _expectedDeliveryChannel;

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
        Receive<ReminderDeliveryObserved>(HandleDeliveryObserved);
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
            if (_definition.Audience is not { } audience)
                throw new InvalidOperationException($"Reminder '{_definition.Id}' is missing a persisted execution audience.");

            _log.Info(
                $"ReminderExecution Initialized: execution_id={_executionId} reminder_id={_definition.Id} session_id={sessionId.Value} audience={audience} source=stored-definition");

            var self = Self;
            var inputQueue = await _handle.InitializeWithQueueAsync(
                Context,
                sessionId,
                new SessionPipelineOptions
                {
                    ChannelType = Channels.ChannelType.Reminder,
                    DefaultAudience = audience,
                    DefaultBoundary = SecurityPolicyDefaults.LocalDaemonBoundary,
                    DefaultPrincipal = PrincipalClassification.VerifiedAutomation,
                    DefaultProvenance = new SourceProvenance
                    {
                        TransportAuthenticity = TransportAuthenticity.LocalProcess,
                        PayloadTaint = PayloadTaint.Trusted,
                        SourceKind = "reminder"
                    },
                    Filter = OutputFilter.TextStreaming | OutputFilter.ToolCalls
                },
                output => self.Tell(new ExecutionOutput(output)));

            var prompt = BuildPrompt(_definition);

            await inputQueue.OfferAsync(new ChannelInput
            {
                SenderId = "reminder-system",
                ChannelId = _definition.Delivery.Address,
                Contents = [new TextContent(prompt)],
                ReceivedAt = _timeProvider.GetUtcNow()
            });

            inputQueue.Complete();
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
    /// <c>MessageSource.AckTarget</c>-propagated <c>CommandAck</c>. On
    /// timeout, <c>CommandNack</c>, or any exception, <c>AckAsync</c> is
    /// NOT called and Akka.Reminders redelivers per its built-in policy.
    /// </summary>
    private async Task InitializeCurrentSessionAsync()
    {
        try
        {
            var sessionId = new SessionId(_definition.Delivery.SessionId!);
            _sessionIdValue = sessionId.Value;
            var originChannelType = _definition.Delivery.OriginChannelType!.Value;

            if (_definition.Audience is not { } audience)
                throw new InvalidOperationException($"Reminder '{_definition.Id}' is missing a persisted execution audience.");

            var reminderDeliveryKey = $"{_definition.Id}:{_dispatchedAt.ToUnixTimeMilliseconds()}";

            _log.Info(
                "ReminderExecution CurrentSession Initialized: execution_id={ExecutionId} reminder_id={ReminderId} session_id={SessionId} origin={Origin} audience={Audience}",
                _executionId, _definition.Id, sessionId.Value, originChannelType, audience);

            var source = new MessageSource
            {
                ChannelType = originChannelType,
                SenderId = "reminder-system",
                MessageId = reminderDeliveryKey,
                TurnId = reminderDeliveryKey,
                Audience = audience,
                Boundary = _definition.Boundary
                    ?? SecurityPolicyDefaults.ResolveBoundary(
                        boundary: null,
                        channelType: originChannelType.ToWireValue(),
                        audience: audience),
                Principal = PrincipalClassification.VerifiedAutomation,
                Provenance = new SourceProvenance
                {
                    TransportAuthenticity = TransportAuthenticity.LocalProcess,
                    PayloadTaint = PayloadTaint.Trusted,
                    SourceKind = "reminder"
                },
                ReceivedAt = _dispatchedAt,
                ReminderId = reminderDeliveryKey
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
                            var observed = await WaitForDeliveryObservationAsync(reminderDeliveryKey, originChannelType);
                            if (!observed)
                            {
                                ReportAndStop(false, $"delivery not observed within {DeliveryObservedTimeout}");
                                break;
                            }
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

    private void HandleDeliveryObserved(ReminderDeliveryObserved observed)
    {
        if (_deliveryObservedTcs is null || _expectedReminderDeliveryKey is null)
            return;

        if (!string.Equals(observed.ReminderDeliveryKey, _expectedReminderDeliveryKey, StringComparison.Ordinal))
            return;

        if (_expectedDeliveryChannel is { } expectedChannel && observed.ChannelType != expectedChannel)
            return;

        _deliveryObservedTcs.TrySetResult(observed);
    }

    private async Task<bool> WaitForDeliveryObservationAsync(string reminderDeliveryKey, ChannelType channelType)
    {
        _expectedReminderDeliveryKey = reminderDeliveryKey;
        _expectedDeliveryChannel = channelType;
        _deliveryObservedTcs = new TaskCompletionSource<ReminderDeliveryObserved>(TaskCreationOptions.RunContinuationsAsynchronously);
        Context.System.EventStream.Subscribe(Self, typeof(ReminderDeliveryObserved));

        try
        {
            var completed = await Task.WhenAny(_deliveryObservedTcs.Task, Task.Delay(DeliveryObservedTimeout));
            if (completed != _deliveryObservedTcs.Task)
            {
                _log.Warning(
                    "reminder_delivery_observation_timeout execution_id={ExecutionId} reminder_id={ReminderId} key={ReminderDeliveryKey} timeout={Timeout}",
                    _executionId, _definition.Id, reminderDeliveryKey, DeliveryObservedTimeout);
                return false;
            }

            var observed = await _deliveryObservedTcs.Task;
            _log.Info(
                "reminder_delivery_observed execution_id={ExecutionId} reminder_id={ReminderId} key={ReminderDeliveryKey} channel={ChannelType}",
                _executionId, _definition.Id, observed.ReminderDeliveryKey, observed.ChannelType);
            return true;
        }
        finally
        {
            Context.System.EventStream.Unsubscribe(Self, typeof(ReminderDeliveryObserved));
            _deliveryObservedTcs = null;
            _expectedReminderDeliveryKey = null;
            _expectedDeliveryChannel = null;
        }
    }

    private async Task TryAckEnvelopeAsync()
    {
        var ackResponse = await _reminderClient!.AckAsync(_envelope!);
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
            ChannelType.Tui => registry.TryGet<SignalRGatewayActorKey>(out var signalr) ? signalr : null,
            ChannelType.SignalR => registry.TryGet<SignalRGatewayActorKey>(out var signalr2) ? signalr2 : null,
            _ => null
        };
    }

    private static string BuildPrompt(ReminderDefinition definition)
    {
        var deliverySection = definition.Delivery.Kind switch
        {
            DeliveryKind.CurrentSession => string.IsNullOrWhiteSpace(definition.DeliveryInstructions)
                ? ""
                : $"\n\nDelivery guidance:\n{definition.DeliveryInstructions}",
            DeliveryKind.Channel => $"\n\nPost the result to {definition.Delivery.Transport} target {definition.Delivery.Address}." +
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

    private void ReportAndStop(bool success, string? errorMessage = null)
    {
        if (_completed)
            return;

        _completed = true;

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
            new ReminderId(_definition.Id),
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
        if (_pendingHistory is not null)
        {
            try
            {
                _historyStore.AppendAsync(new ReminderId(_definition.Id), _pendingHistory)
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

    private sealed record ExecutionStarted;
    private sealed record ExecutionOutput(SessionOutput Output);
}
