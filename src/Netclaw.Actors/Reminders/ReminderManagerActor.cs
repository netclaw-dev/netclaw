// -----------------------------------------------------------------------
// <copyright file="ReminderManagerActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Akka.Actor;
using Akka.Event;
using Akka.Reminders;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using AkkaReminderProtocol = Akka.Reminders.ReminderProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Singleton actor that mediates between Akka.Reminders and reminder execution.
/// Schedules durable timer entries and resolves execution behavior from
/// file-backed reminder definitions.
/// </summary>
public sealed partial class ReminderManagerActor : ReceiveActor
{
    public const string ShardRegionName = "netclaw-reminders";
    public const string EntityId = "manager";

    /// <summary>
    /// Maximum number of concurrent reminder executions. Not configurable —
    /// if we ever need to tune this, add a knob then.
    /// </summary>
    internal const int MaxConcurrentExecutions = 3;

    /// <summary>
    /// Consecutive execution failures after which a reminder is auto-paused.
    /// Not configurable. Must stay strictly below Akka.Reminders'
    /// <c>MaxDeliveryAttempts</c> (default 10) so Netclaw's auto-pause fires
    /// first — the two counters are kept out of conflict by inspection.
    /// </summary>
    internal const int FailurePauseThreshold = 5;

    /// <summary>Recent run records returned by the per-reminder status query.</summary>
    internal const int RecentHistoryCount = 5;

    internal static readonly TimeSpan SettlementMargin = TimeSpan.FromMinutes(1);

    private readonly ISessionPipeline _pipeline;
    private readonly EffectivePolicyDefaults _defaults;
    private readonly SchedulingConfig _schedulingConfig;
    private readonly TimeProvider _timeProvider;
    private readonly ReminderDefinitionStore _definitionStore;
    private readonly ReminderHistoryStore _historyStore;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly IReminderChannelNotifier _channelNotifier;
    private readonly ILoggingAdapter _log;

    private IReminderClient? _client;

    private readonly ActiveExecutionTracker _activeExecutions = new();
    private readonly Dictionary<ReminderId, int> _skipCounts = [];

    public ReminderManagerActor(
        ISessionPipeline pipeline,
        EffectivePolicyDefaults defaults,
        SchedulingConfig schedulingConfig,
        TimeProvider timeProvider,
        ReminderDefinitionStore definitionStore,
        ReminderHistoryStore historyStore,
        IOperationalNotificationSink notificationSink,
        IReminderChannelNotifier channelNotifier)
    {
        _pipeline = pipeline;
        _defaults = defaults;
        _schedulingConfig = schedulingConfig;
        _timeProvider = timeProvider;
        _definitionStore = definitionStore;
        _historyStore = historyStore;
        _notificationSink = notificationSink;
        _channelNotifier = channelNotifier;
        _log = Context.GetLogger();

        ReceiveAsync<SaveReminderCommand>(HandleSaveAsync);
        ReceiveAsync<CancelReminderCommand>(HandleCancelAsync);
        ReceiveAsync<DeleteReminderCommand>(HandlePermanentDeleteAsync);
        ReceiveAsync<DisableReminderCommand>(HandleDisableAsync);
        ReceiveAsync<EnableReminderCommand>(HandleEnableAsync);
        ReceiveAsync<ListRemindersCommand>(HandleListAsync);
        ReceiveAsync<GetReminderCommand>(HandleGetAsync);

        ReceiveAsync<ReminderEnvelope<ReminderPayload>>(HandleReminderFiredAsync);
        ReceiveAsync<ReminderExecutionCompleted>(HandleExecutionOutcomeAsync);
        ReceiveAsync<ReminderExecutionTerminated>(HandleExecutionTerminatedAsync);

        ReceiveAsync<ReconcileReminders>(_ => HandleReconcileAsync());
        Receive<GetReminderHealthQuery>(_ => HandleGetHealth());
        ReceiveAsync<GetReminderStatusQuery>(HandleGetStatusAsync);
    }

    protected override void PreStart()
    {
        var extension = ReminderClientExtension.Get(Context.System);
        _client = extension.CreateClient(new ReminderEntity(ShardRegionName, EntityId));
        _log.Info("ReminderManagerActor started (scheduling enabled={0})", _schedulingConfig.Enabled);

        if (!_schedulingConfig.Enabled)
        {
            _log.Info("Scheduling is disabled — skipping reminder reconciliation and execution");
            return;
        }

        // The store can be constructed before all persisted files are present.
        // Rescan at the actor's startup boundary so schema alerts reflect the
        // authoritative on-disk state rather than constructor timing.
        _definitionStore.List();
        EmitDroppedInvalidDefinitionAlerts();
        EmitRejectedLegacyDefinitionAlerts();

        Self.Tell(ReconcileReminders.Instance);
    }

    protected override SupervisorStrategy SupervisorStrategy() =>
        new OneForOneStrategy(_ => Directive.Stop);

    private void EmitDroppedInvalidDefinitionAlerts()
    {
        var dropped = _definitionStore.ConsumeDroppedInvalidDefinitions();
        if (dropped.Count == 0)
            return;

        var droppedIds = string.Join(", ", dropped.Select(x => x.ReminderId));
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "reminder.schema.invalid_dropped",
            AlertType.ReminderSchemaDropped,
            $"Dropped {dropped.Count} invalid reminder definition(s) during startup. Re-create reminder IDs: {droppedIds}.",
            AlertSeverity.Warning,
            source: "startup",
            context: new Dictionary<string, string>
            {
                ["droppedCount"] = dropped.Count.ToString(),
                ["droppedIds"] = droppedIds
            }));

        _log.Warning("Dropped {0} invalid reminder definition(s) during startup: {1}", dropped.Count, droppedIds);
    }

    private void EmitRejectedLegacyDefinitionAlerts()
    {
        var rejected = _definitionStore.ConsumeRejectedLegacyDefinitions();
        if (rejected.Count == 0)
            return;

        var rejectedIds = string.Join(", ", rejected.Select(x => x.ReminderId));
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "reminder.schema.legacy_rejected",
            AlertType.ReminderSchemaDropped,
            $"Rejected {rejected.Count} legacy reminder definition(s) missing trust fields during startup. Repair or recreate reminder IDs: {rejectedIds}.",
            AlertSeverity.Warning,
            source: "startup",
            context: new Dictionary<string, string>
            {
                ["rejectedCount"] = rejected.Count.ToString(),
                ["rejectedIds"] = rejectedIds
            }));

        _log.Warning(
            "Rejected {0} legacy reminder definition(s) missing trust fields during startup: {1}",
            rejected.Count,
            rejectedIds);
    }

    private async Task HandleSaveAsync(SaveReminderCommand cmd)
    {
        var replyTo = Sender;

        static ReminderSavedResponse ValidationFailure(ReminderId id, string title, string message)
            => new(
                id,
                title,
                Success: false,
                NextFire: null,
                Error: ReminderSaveError.Validation,
                ErrorMessage: message);

        if (cmd.Definition is null)
        {
            replyTo.Tell(new ReminderSavedResponse(
                new ReminderId("unknown"),
                "unknown",
                Success: false,
                NextFire: null,
                Error: ReminderSaveError.Validation,
                ErrorMessage: "Reminder definition is required."));
            return;
        }

        var title = cmd.Definition.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            replyTo.Tell(new ReminderSavedResponse(
                cmd.Definition.Id,
                string.Empty,
                Success: false,
                NextFire: null,
                Error: ReminderSaveError.Validation,
                ErrorMessage: "Reminder title is required."));
            return;
        }

        var id = !string.IsNullOrWhiteSpace(cmd.Definition.Id.Value)
            ? cmd.Definition.Id
            : ReminderIdGenerator.Generate(title);

        var exists = _definitionStore.Exists(id);

        switch (cmd.WriteMode)
        {
            case ReminderWriteMode.CreateOnly when exists:
                replyTo.Tell(new ReminderSavedResponse(
                    id,
                    title,
                    Success: false,
                    NextFire: null,
                    Error: ReminderSaveError.Conflict,
                    ErrorMessage: $"Reminder '{id.Value}' already exists."));
                return;

            case ReminderWriteMode.Replace when !exists:
                replyTo.Tell(new ReminderSavedResponse(
                    id,
                    title,
                    Success: false,
                    NextFire: null,
                    Error: ReminderSaveError.NotFound,
                    ErrorMessage: $"Reminder '{id.Value}' was not found."));
                return;
        }

        var now = _timeProvider.GetUtcNow();
        var authorization = ValidateRequestedAudience(cmd.Definition.Audience, cmd.Authorization);
        if (!authorization.IsSuccess)
        {
            replyTo.Tell(ValidationFailure(id, title, authorization.ErrorMessage!));
            return;
        }

        // Non-null on the success path — IsSuccess was checked above, and a
        // successful ReminderAudienceAuthorizationResult always carries an audience.
        var effectiveAudience = authorization.EffectiveAudience!.Value;
        var boundaryValidation = ValidateRequestedBoundary(cmd.Definition.Boundary, effectiveAudience);
        if (!boundaryValidation.IsSuccess)
        {
            replyTo.Tell(ValidationFailure(id, title, boundaryValidation.ErrorMessage!));
            return;
        }

        var effectiveBoundary = boundaryValidation.NormalizedBoundary!.Value;

        var normalized = cmd.Definition with
        {
            Id = id,
            Title = title,
            Audience = effectiveAudience,
            Boundary = effectiveBoundary,
            CreatedBy = string.IsNullOrWhiteSpace(cmd.Definition.CreatedBy)
                ? "system"
                : cmd.Definition.CreatedBy
        };

        if (normalized.Schedule.Type == ReminderScheduleType.OneShot && normalized.ExpiresAt is not null)
        {
            replyTo.Tell(ValidationFailure(id, title, "expires_at is not applicable to one-shot reminders."));
            return;
        }

        if (exists)
        {
            var existing = _definitionStore.Get(id);
            normalized.CreatedAtMs = existing?.CreatedAtMs ?? (normalized.CreatedAtMs > 0 ? normalized.CreatedAtMs : now.ToUnixTimeMilliseconds());
        }
        else
        {
            normalized.CreatedAtMs = normalized.CreatedAtMs > 0 ? normalized.CreatedAtMs : now.ToUnixTimeMilliseconds();
        }

        normalized.UpdatedAtMs = now.ToUnixTimeMilliseconds();

        if (exists)
        {
            await CancelScheduleOnlyAsync(id);
        }

        DateTimeOffset? nextFire = null;
        if (normalized.Enabled)
        {
            var scheduleResult = await ScheduleDefinitionAsync(
                normalized,
                rescheduleFromNow: exists || cmd.WriteMode is not ReminderWriteMode.CreateOnly);

            if (!scheduleResult.IsSuccess)
            {
                replyTo.Tell(new ReminderSavedResponse(
                    id,
                    normalized.Title,
                    Success: false,
                    NextFire: null,
                    Error: ReminderSaveError.Validation,
                    ErrorMessage: scheduleResult.ErrorMessage));
                return;
            }

            nextFire = scheduleResult.NextFire;

            // Persist the (possibly rescheduled) interval first-fire time so a daemon
            // restart re-uses the same anchor instead of resetting "now + interval".
            if (normalized.Schedule.Type == ReminderScheduleType.Interval && nextFire is not null)
            {
                normalized = normalized with
                {
                    Schedule = normalized.Schedule with { FireAt = nextFire }
                };
            }
        }
        else
        {
            await CancelScheduleOnlyAsync(id);
        }

        _definitionStore.Save(normalized);

        _log.Info("Saved reminder '{0}' (enabled={1})", normalized.Id, normalized.Enabled);

        replyTo.Tell(new ReminderSavedResponse(
            id,
            normalized.Title,
            Success: true,
            NextFire: nextFire));
    }

    private static ReminderAudienceAuthorizationResult ValidateRequestedAudience(
        TrustAudience requestedAudience,
        ReminderAudienceAuthorizationContext? authorization)
    {
        if (authorization?.SourceAudience is not { } sourceAudience)
        {
            return ReminderAudienceAuthorizationResult.Fail(
                "Reminder audience authorization context is required.");
        }

        var effectiveAudience = requestedAudience;
        if (effectiveAudience > sourceAudience)
        {
            var sourceDescription = string.IsNullOrWhiteSpace(authorization.SourceDescription)
                ? sourceAudience.ToWireValue()
                : authorization.SourceDescription;

            return ReminderAudienceAuthorizationResult.Fail(
                $"Requested audience '{effectiveAudience.ToWireValue()}' exceeds creator authority '{sourceDescription}' ({sourceAudience.ToWireValue()}).");
        }

        return ReminderAudienceAuthorizationResult.Success(effectiveAudience);
    }

    private static ReminderBoundaryValidationResult ValidateRequestedBoundary(
        TrustBoundary requestedBoundary,
        TrustAudience effectiveAudience)
    {
        if (!SecurityPolicyDefaults.TryNormalizeBoundary(requestedBoundary.Value, out var normalizedBoundary))
        {
            return ReminderBoundaryValidationResult.Fail(
                $"Reminder boundary '{requestedBoundary}' is not a recognized trust boundary.");
        }

        if (!SecurityPolicyDefaults.IsBoundaryCompatibleWithAudience(normalizedBoundary, effectiveAudience))
        {
            return ReminderBoundaryValidationResult.Fail(
                $"Reminder boundary '{normalizedBoundary}' is not allowed for audience '{effectiveAudience.ToWireValue()}'.");
        }

        return ReminderBoundaryValidationResult.Success(normalizedBoundary);
    }

    private async Task HandleCancelAsync(CancelReminderCommand cmd)
    {
        var replyTo = Sender;
        var response = await DisableReminderInternalAsync(cmd.Id);

        _log.Info("Cancel reminder '{0}': {1}", cmd.Id.Value, response.Found ? "disabled" : "not found");
        replyTo.Tell(new ReminderCancelledResponse(cmd.Id, response.Found));
    }

    private async Task HandlePermanentDeleteAsync(DeleteReminderCommand cmd)
    {
        var replyTo = Sender;
        var found = _definitionStore.Exists(cmd.Id);
        await DeleteReminderInternalAsync(cmd.Id);

        _log.Info("Permanently delete reminder '{0}': {1}", cmd.Id.Value, found ? "deleted" : "not found");
        replyTo.Tell(new ReminderDeletedResponse(cmd.Id, found));
    }

    private async Task HandleDisableAsync(DisableReminderCommand cmd)
    {
        var replyTo = Sender;
        var response = await DisableReminderInternalAsync(cmd.Id);
        replyTo.Tell(response);
    }

    private async Task HandleEnableAsync(EnableReminderCommand cmd)
    {
        var replyTo = Sender;
        var response = await EnableReminderInternalAsync(cmd.Id);
        replyTo.Tell(response);
    }

    private async Task<ReminderStateResponse> DisableReminderInternalAsync(ReminderId id)
    {
        var definition = _definitionStore.Get(id);
        if (definition is null)
            return new ReminderStateResponse(id, Found: false, Enabled: false, ErrorMessage: "Reminder not found.");

        if (!definition.Enabled)
            return new ReminderStateResponse(id, Found: true, Enabled: false);

        definition = definition with
        {
            Enabled = false,
            UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _definitionStore.Save(definition);
        await CancelScheduleOnlyAsync(id);

        _skipCounts.Remove(id);

        _log.Info("Disabled reminder '{0}'", id.Value);
        return new ReminderStateResponse(id, Found: true, Enabled: false);
    }

    /// <summary>
    /// Permanently removes a reminder definition, its schedule, history, and process state.
    /// Only an explicit delete command uses this path.
    /// </summary>
    private async Task DeleteReminderInternalAsync(ReminderId id)
    {
        await CancelScheduleOnlyAsync(id);
        _skipCounts.Remove(id);

        try
        {
            _historyStore.DeleteHistory(id);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to delete history for reminder '{0}'", id.Value);
            throw;
        }

        // Delete the definition last so reconciliation can retry a partial cleanup.
        _definitionStore.Delete(id);
    }

    private async Task<ReminderStateResponse> EnableReminderInternalAsync(ReminderId id)
    {
        var definition = _definitionStore.Get(id);
        if (definition is null)
            return new ReminderStateResponse(id, Found: false, Enabled: false, ErrorMessage: "Reminder not found.");

        var candidate = definition with
        {
            Enabled = true,
            ConsecutiveFailures = 0,
            TerminalOutcome = null,
            UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        var scheduleResult = await ScheduleDefinitionAsync(candidate, rescheduleFromNow: true);
        if (!scheduleResult.IsSuccess)
        {
            return new ReminderStateResponse(
                id,
                Found: true,
                Enabled: definition.Enabled,
                ErrorMessage: scheduleResult.ErrorMessage);
        }

        _definitionStore.Save(candidate);
        _log.Info("Enabled reminder '{0}'", id.Value);

        return new ReminderStateResponse(
            id,
            Found: true,
            Enabled: true,
            NextFire: scheduleResult.NextFire);
    }

    private async Task HandleListAsync(ListRemindersCommand cmd)
    {
        var replyTo = Sender;
        try
        {
            var definitions = _definitionStore.List();
            var schedules = await ListScheduledRemindersAsync();

            var infos = definitions
                .Where(d => cmd.IncludeDisabled || d.Enabled)
                .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
                .Select(d => ToReminderInfo(d, schedules.GetValueOrDefault(d.Id.Value)))
                .ToList();

            replyTo.Tell(new ReminderListResponse(infos));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error listing reminders");
            replyTo.Tell(new ReminderListResponse([]));
        }
    }

    private async Task HandleGetAsync(GetReminderCommand cmd)
    {
        var replyTo = Sender;
        try
        {
            var definition = _definitionStore.Get(cmd.Id);
            if (definition is null)
            {
                replyTo.Tell(new GetReminderResponse(null));
                return;
            }

            var schedules = await ListScheduledRemindersAsync();
            var info = ToReminderInfo(definition, schedules.GetValueOrDefault(definition.Id.Value));

            replyTo.Tell(new GetReminderResponse(info));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error getting reminder '{0}'", cmd.Id.Value);
            replyTo.Tell(new GetReminderResponse(null));
        }
    }

    private async Task HandleReminderFiredAsync(ReminderEnvelope<ReminderPayload> envelope)
    {
        if (!_schedulingConfig.Enabled)
        {
            _log.Warning("Scheduling is disabled — ignoring fired reminder and acking envelope");
            await _client!.AckAsync(envelope);
            return;
        }

        var payload = envelope.Message;
        var reminderId = payload.Id;
        var definition = _definitionStore.Get(reminderId);

        if (definition is null)
        {
            _log.Error("Reminder fired for missing definition '{0}'. Cancelling orphaned schedule.", reminderId.Value);
            await CancelScheduleOnlyAsync(reminderId);
            await _client!.AckAsync(envelope);
            return;
        }

        if (!definition.Enabled)
        {
            _log.Warning("Reminder '{0}' fired while disabled. Cancelling any lingering schedule.", reminderId.Value);
            await CancelScheduleOnlyAsync(reminderId);
            await _client!.AckAsync(envelope);
            return;
        }

        if (definition.Schedule.Type is not ReminderScheduleType.OneShot
            && definition.ExpiresAt is { } expiresAt
            && expiresAt <= _timeProvider.GetUtcNow())
        {
            _log.Info("Reminder '{0}' has expired (expiresAt={1}), disabling", reminderId.Value, expiresAt);
            await DisableReminderInternalAsync(reminderId);
            await _client!.AckAsync(envelope);
            return;
        }

        _log.Info("Reminder fired: id='{0}', title='{1}', schedule_type={2}",
            reminderId.Value, definition.Title, definition.Schedule.Type);

        // Cron reminders are implemented as recurring single-shot schedules.
        if (definition.Schedule.Type == ReminderScheduleType.Cron)
        {
            var scheduleResult = await ScheduleDefinitionAsync(definition, rescheduleFromNow: true);
            if (!scheduleResult.IsSuccess)
            {
                _log.Warning("Failed to reschedule cron reminder '{0}': {1}", reminderId.Value, scheduleResult.ErrorMessage);
            }
        }

        if (_activeExecutions.TryGet(reminderId, out var activeExecution))
        {
            RecordSkippedDuplicate(reminderId, definition.Title, "active");
            if (IsSameDeliveryAttempt(activeExecution.Envelope, envelope))
                return;

            var sameOccurrence = activeExecution.Envelope.Key == envelope.Key
                                 && activeExecution.Envelope.DueTimeUtc == envelope.DueTimeUtc;
            await SettleBlockedOccurrenceAsync(
                definition,
                envelope,
                nack: definition.Schedule.Type == ReminderScheduleType.OneShot || sameOccurrence,
                "Another execution for this reminder is active.");
            return;
        }

        if (_activeExecutions.Count >= MaxConcurrentExecutions)
        {
            _log.Info("Concurrency limit reached ({0}), settling blocked reminder '{1}'",
                MaxConcurrentExecutions, reminderId.Value);
            if (definition.Schedule.Type != ReminderScheduleType.OneShot)
                RecordSkippedDuplicate(reminderId, definition.Title, "capacity");
            await SettleBlockedOccurrenceAsync(
                definition,
                envelope,
                nack: definition.Schedule.Type == ReminderScheduleType.OneShot,
                "Reminder execution capacity is unavailable.");
            return;
        }

        if (!HasSafeExecutionLease(envelope, _timeProvider.GetUtcNow()))
        {
            var isOneShot = definition.Schedule.Type == ReminderScheduleType.OneShot;
            if (isOneShot)
            {
                _log.Warning(
                    "Reminder '{0}' arrived too late to finish before its delivery deadline. Returning it to the scheduler for retry.",
                    reminderId.Value);
            }
            else
            {
                _log.Warning(
                    "Recurring reminder '{0}' arrived too late to finish before its delivery deadline. Skipping this occurrence.",
                    reminderId.Value);
                RecordSkippedDuplicate(reminderId, definition.Title, "lease");
            }

            await SettleBlockedOccurrenceAsync(
                definition,
                envelope,
                nack: isOneShot,
                "The reminder arrived too late to finish before its delivery deadline.");
            return;
        }

        // Issue #1803: every delivery mode retains its envelope until the
        // execution actor confirms success or reports a known failure.
        StartExecution(definition, envelope);
    }

    private static bool IsSameDeliveryAttempt(
        ReminderEnvelope<ReminderPayload> active,
        ReminderEnvelope<ReminderPayload> candidate) =>
        active.Key == candidate.Key
        && active.DueTimeUtc == candidate.DueTimeUtc
        && active.Deadline == candidate.Deadline;

    private static bool HasSafeExecutionLease(
        ReminderEnvelope<ReminderPayload> envelope,
        DateTimeOffset now) =>
        envelope.Deadline.IsInfinite
        || envelope.Deadline.UtcDateTime - now >= ReminderExecutionActor.ExecutionAttemptTimeout + SettlementMargin;

    private async Task SettleBlockedOccurrenceAsync(
        ReminderDefinition definition,
        ReminderEnvelope<ReminderPayload> envelope,
        bool nack,
        string reason)
    {
        try
        {
            if (nack)
            {
                var response = await _client!.NackAsync(envelope, reason);
                if (response.ResponseCode is ReminderNackResponseCode.Error or ReminderNackResponseCode.NotFound)
                {
                    EmitSettlementFailure(
                        definition,
                        $"Negative acknowledgement returned {response.ResponseCode}: {response.Message}");
                }

                return;
            }

            var ack = await _client!.AckAsync(envelope);
            if (ack.ResponseCode != ReminderAckResponseCode.Success)
            {
                EmitSettlementFailure(
                    definition,
                    $"Acknowledgement returned {ack.ResponseCode}: {ack.Message}");
            }
        }
        catch (Exception ex)
        {
            EmitSettlementFailure(definition, ex.Message, ex);
        }
    }

    /// <summary>
    /// Records an occurrence that Netclaw skips or returns to Akka.Reminders.
    /// The status command exposes this process-local skip count.
    /// </summary>
    private void RecordSkippedDuplicate(ReminderId reminderId, string title, string source)
    {
        var count = _skipCounts.GetValueOrDefault(reminderId) + 1;
        _skipCounts[reminderId] = count;
        _log.Warning(
            "reminder_skipped_duplicate_execution reminder_id={0} title={1} source={2} skip_count={3}",
            reminderId.Value, title, source, count);
    }

    /// <summary>
    /// Posts an operator-facing failure notice to a reminder's destination
    /// channel. Only Channel-delivery reminders have such a channel; CurrentSession
    /// and None failures are surfaced via the operational alert sink instead. The
    /// notifier is fire-and-forget — never blocks or throws into the manager.
    /// </summary>
    private void PostFailureNoticeToChannel(ReminderDefinition? definition, string text)
    {
        if (definition is not { Delivery.Kind: DeliveryKind.Channel })
            return;

        var target = ReminderExecutionActor.ResolveChannelDeliveryTarget(definition);
        if (target is null)
        {
            _log.Warning(
                "Reminder '{0}' failed but its channel delivery target could not be resolved; no channel notice posted.",
                definition.Id.Value);
            return;
        }

        _channelNotifier.NotifyFailure(target, text);
    }

    private async Task HandleExecutionOutcomeAsync(ReminderExecutionCompleted outcome)
    {
        var replyTo = Sender;
        if (!_activeExecutions.TryGet(outcome.Id, out var execution)
            || execution.ExecutionId != outcome.ExecutionId)
        {
            replyTo.Tell(new ReminderExecutionAccepted(outcome.ExecutionId));
            return;
        }

        try
        {
            await SettleExecutionOutcomeAsync(outcome, execution);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unexpected reminder settlement failure for '{0}'", outcome.Id.Value);
            var definition = _definitionStore.Get(outcome.Id);
            if (definition is not null)
                EmitSettlementFailure(definition, ex.Message, ex);
        }
        finally
        {
            _activeExecutions.TryRemove(outcome.Id, outcome.ExecutionId, out _);
            replyTo.Tell(new ReminderExecutionAccepted(outcome.ExecutionId));
        }
    }

    private async Task HandleExecutionTerminatedAsync(ReminderExecutionTerminated terminated)
    {
        if (!_activeExecutions.TryRemove(terminated.Id, terminated.ExecutionId, out var execution))
            return;

        const string reason = "Reminder execution actor terminated unexpectedly.";
        var now = _timeProvider.GetUtcNow();
        var definition = _definitionStore.Get(terminated.Id);
        var sessionId = definition?.Delivery.Kind == DeliveryKind.CurrentSession
            ? definition.Delivery.SessionId ?? $"reminder/{terminated.Id}/unknown"
            : $"reminder/{terminated.Id}/{execution.Envelope.DueTimeUtc.ToUnixTimeMilliseconds()}";
        var history = new HistoryRecord(
            execution.StartedAt,
            Success: false,
            DurationMs: (long)(now - execution.StartedAt).TotalMilliseconds,
            sessionId,
            reason);

        try
        {
            await SettleExecutionOutcomeAsync(new ReminderExecutionCompleted(
                terminated.ExecutionId,
                terminated.Id,
                Success: false,
                history,
                reason), execution);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unexpected termination settlement failed for reminder '{0}'", terminated.Id.Value);
            if (definition is not null)
                EmitSettlementFailure(definition, ex.Message, ex);
        }
    }

    private async Task SettleExecutionOutcomeAsync(
        ReminderExecutionCompleted outcome,
        ActiveReminderExecution execution)
    {
        await AppendHistorySafelyAsync(outcome.Id, outcome.History);

        var definition = _definitionStore.Get(outcome.Id);
        if (outcome.Success)
        {
            await SettleSuccessfulExecutionAsync(outcome, execution, definition);
            return;
        }

        await SettleFailedExecutionAsync(outcome, execution, definition);
    }

    private async Task AppendHistorySafelyAsync(ReminderId id, HistoryRecord history)
    {
        try
        {
            await _historyStore.AppendAsync(id, history);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to write execution history for reminder '{0}'", id.Value);
        }
    }

    private async Task SettleSuccessfulExecutionAsync(
        ReminderExecutionCompleted outcome,
        ActiveReminderExecution execution,
        ReminderDefinition? definition)
    {
        if (definition is not null)
        {
            try
            {
                _definitionStore.Save(definition with
                {
                    ConsecutiveFailures = 0,
                    UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                });
            }
            catch (Exception ex)
            {
                EmitSettlementFailure(definition, ex.Message, ex);
                return;
            }
        }

        AkkaReminderProtocol.ReminderAckResponse ack;
        try
        {
            ack = await _client!.AckAsync(execution.Envelope);
        }
        catch (Exception ex)
        {
            if (definition is not null)
                EmitSettlementFailure(definition, ex.Message, ex);
            return;
        }

        if (ack.ResponseCode != ReminderAckResponseCode.Success)
        {
            if (definition is not null)
            {
                EmitSettlementFailure(
                    definition,
                    ack.Message ?? $"Reminder acknowledgement returned {ack.ResponseCode}.");
            }

            return;
        }

        if (definition is { Schedule.Type: ReminderScheduleType.OneShot })
            await DeleteReminderInternalAsync(outcome.Id);

        _log.Info("Reminder '{0}' execution completed successfully", outcome.Id.Value);
    }

    private async Task SettleFailedExecutionAsync(
        ReminderExecutionCompleted outcome,
        ActiveReminderExecution execution,
        ReminderDefinition? definition)
    {
        var reason = string.IsNullOrWhiteSpace(outcome.ErrorMessage)
            ? "Reminder execution failed."
            : outcome.ErrorMessage;
        var count = (definition?.ConsecutiveFailures ?? 0) + 1;
        var thresholdReached = count >= FailurePauseThreshold;

        if (definition is not null)
        {
            try
            {
                definition = definition with
                {
                    ConsecutiveFailures = count,
                    Enabled = thresholdReached ? false : definition.Enabled,
                    TerminalOutcome = thresholdReached ? ReminderTerminalOutcome.Failed : definition.TerminalOutcome,
                    UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                };
                _definitionStore.Save(definition);
            }
            catch (Exception ex)
            {
                EmitSettlementFailure(definition, ex.Message, ex);
                return;
            }
        }

        AkkaReminderProtocol.ReminderNackResponse? nack = null;
        try
        {
            nack = await _client!.NackAsync(execution.Envelope, reason);
        }
        catch (Exception ex)
        {
            if (definition is not null)
                EmitSettlementFailure(definition, ex.Message, ex);
        }

        var occurrenceTerminal = nack?.ResponseCode is ReminderNackResponseCode.Failed
            or ReminderNackResponseCode.Expired;
        if (nack?.ResponseCode is ReminderNackResponseCode.Error or ReminderNackResponseCode.NotFound)
        {
            if (definition is not null)
            {
                EmitSettlementFailure(
                    definition,
                    nack.Message ?? $"Negative acknowledgement returned {nack.ResponseCode}.");
            }
        }

        ReportExecutionFailure(
            outcome.Id,
            definition,
            count,
            reason,
            willDisable: thresholdReached || occurrenceTerminal);

        if (thresholdReached || occurrenceTerminal)
        {
            if (!thresholdReached && definition is not null)
            {
                try
                {
                    definition = definition with
                    {
                        Enabled = false,
                        TerminalOutcome = ReminderTerminalOutcome.Failed,
                        UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                    };
                    _definitionStore.Save(definition);
                }
                catch (Exception ex)
                {
                    EmitSettlementFailure(definition, ex.Message, ex);
                }
            }

            await CancelScheduleOnlyAsync(outcome.Id);
        }
    }

    private void ReportExecutionFailure(
        ReminderId id,
        ReminderDefinition? definition,
        int count,
        string reason,
        bool willDisable)
    {
        var title = definition?.Title ?? id.Value;
        _log.Warning("Reminder '{0}' execution failed ({1}/{2}): {3}",
            id.Value, count, FailurePauseThreshold, reason);

        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "reminder.execution.failed",
            AlertType.ReminderExecutionFailed,
            $"Reminder '{title}' execution failed: {reason}",
            AlertSeverity.Warning,
            source: id.Value,
            context: new Dictionary<string, string>
            {
                ["reminderId"] = id.Value,
                ["title"] = title,
                ["error"] = reason
            }));

        if (!willDisable)
        {
            PostFailureNoticeToChannel(definition, $"Reminder \"{title}\" failed: {reason}");
            return;
        }

        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "reminder.auto_disabled",
            AlertType.ReminderAutoDisabled,
            $"Reminder '{title}' disabled after {count} consecutive failures or a terminal occurrence",
            AlertSeverity.Critical,
            source: id.Value,
            context: new Dictionary<string, string>
            {
                ["reminderId"] = id.Value,
                ["title"] = title,
                ["failureCount"] = count.ToString()
            }));

        PostFailureNoticeToChannel(
            definition,
            $"Reminder \"{title}\" was automatically disabled after {count} consecutive failures or a terminal occurrence. Last error: {reason}");
    }

    private void EmitSettlementFailure(ReminderDefinition definition, string reason, Exception? exception = null)
    {
        if (exception is null)
            _log.Warning("Reminder settlement failed for '{0}': {1}", definition.Id.Value, reason);
        else
            _log.Error(exception, "Reminder settlement failed for '{0}': {1}", definition.Id.Value, reason);

        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "reminder.settlement.failed",
            AlertType.ReminderExecutionFailed,
            $"Reminder '{definition.Title}' settlement failed: {reason}",
            AlertSeverity.Warning,
            source: definition.Id.Value,
            context: new Dictionary<string, string>
            {
                ["reminderId"] = definition.Id.Value,
                ["title"] = definition.Title,
                ["error"] = reason
            }));
    }

    private async Task HandleReconcileAsync()
    {
        var sender = Sender; // capture before any await
        try
        {
            var scheduled = await ListScheduledRemindersAsync();
            var definitions = _definitionStore.List();
            var definitionsById = definitions.ToDictionary(d => d.Id.Value, StringComparer.Ordinal);

            var deletedCompletedOneShots = 0;
            foreach (var definition in definitions.Where(d =>
                         !d.Enabled &&
                         d.Schedule.Type == ReminderScheduleType.OneShot &&
                         d.TerminalOutcome == ReminderTerminalOutcome.Completed))
            {
                await DeleteReminderInternalAsync(definition.Id);
                definitionsById.Remove(definition.Id.Value);
                deletedCompletedOneShots++;
            }

            var cancelledOrphans = 0;
            foreach (var (id, _) in scheduled)
            {
                if (!definitionsById.TryGetValue(id, out var definition) || !definition.Enabled)
                {
                    await CancelScheduleOnlyAsync(new ReminderId(id));
                    cancelledOrphans++;
                }
            }

            var restoredSchedules = 0;
            foreach (var definition in definitions.Where(d => d.Enabled))
            {
                if (scheduled.ContainsKey(definition.Id.Value))
                    continue;

                if (definition.Schedule.Type == ReminderScheduleType.OneShot
                    && definition.Schedule.FireAt <= _timeProvider.GetUtcNow())
                {
                    continue;
                }

                var result = await ScheduleDefinitionAsync(definition, rescheduleFromNow: true);
                if (result.IsSuccess)
                    restoredSchedules++;
            }

            // Issue #1803: a past due time and the absence of a schedule do not prove success.
            var now = _timeProvider.GetUtcNow();
            var softDeletedOneShots = 0;
            foreach (var definition in definitions.Where(d =>
                         d.Enabled &&
                         d.Schedule.Type == ReminderScheduleType.OneShot &&
                         d.Schedule.FireAt <= now))
            {
                var occurrence = await GetOccurrenceStatusAsync(definition);
                if (occurrence is null)
                {
                    _log.Warning(
                        "Past one-shot reminder '{0}' has no durable occurrence status. The definition remains enabled for operator review.",
                        definition.Id.Value);
                    continue;
                }

                var outcome = occurrence.CompletionStatus switch
                {
                    Akka.Reminders.Storage.ReminderCompletionStatus.Delivered => ReminderTerminalOutcome.Completed,
                    Akka.Reminders.Storage.ReminderCompletionStatus.Failed => ReminderTerminalOutcome.Failed,
                    Akka.Reminders.Storage.ReminderCompletionStatus.Expired => ReminderTerminalOutcome.Failed,
                    Akka.Reminders.Storage.ReminderCompletionStatus.Cancelled => ReminderTerminalOutcome.Failed,
                    _ => (ReminderTerminalOutcome?)null
                };

                if (outcome is null)
                    continue;

                if (outcome == ReminderTerminalOutcome.Completed)
                {
                    await DeleteReminderInternalAsync(definition.Id);
                    deletedCompletedOneShots++;
                    continue;
                }

                var terminalDefinition = definition with
                {
                    Enabled = false,
                    TerminalOutcome = outcome,
                    UpdatedAtMs = now.ToUnixTimeMilliseconds()
                };
                _definitionStore.Save(terminalDefinition);
                softDeletedOneShots++;
            }

            // Disable expired recurring reminders that haven't fired since expiration.
            var disabledExpired = 0;
            foreach (var definition in definitions.Where(d =>
                         d.Enabled &&
                         d.Schedule.Type is not ReminderScheduleType.OneShot &&
                         d.ExpiresAt is { } ea && ea <= now))
            {
                await DisableReminderInternalAsync(definition.Id);
                disabledExpired++;
            }

            if (cancelledOrphans > 0 || restoredSchedules > 0 || softDeletedOneShots > 0
                || disabledExpired > 0 || deletedCompletedOneShots > 0)
            {
                _log.Info("Reminder reconcile complete: cancelled_orphans={0}, restored={1}, soft_deleted_oneshots={2}, disabled_expired={3}, deleted_completed_oneshots={4}",
                    cancelledOrphans,
                    restoredSchedules,
                    softDeletedOneShots,
                    disabledExpired,
                    deletedCompletedOneShots);
            }

            // Only ack external callers — skip Self.Tell from PreStart
            if (!sender.Equals(Self))
                sender.Tell(new ReconcileCompleted(cancelledOrphans, restoredSchedules, softDeletedOneShots, disabledExpired));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Reminder reconcile failed");

            if (!sender.Equals(Self))
                sender.Tell(new Status.Failure(ex));
        }
    }

    private void StartExecution(
        ReminderDefinition definition,
        ReminderEnvelope<ReminderPayload> envelope)
    {
        var executionId = Guid.NewGuid();
        var startedAt = _timeProvider.GetUtcNow();
        _activeExecutions.Add(definition.Id, executionId, envelope, startedAt);

        var actorName = $"exec-{SanitizeActorName(definition.Id.Value)}-{startedAt.ToUnixTimeMilliseconds()}";
        var executionActor = Context.ActorOf(
            ReminderExecutionActor.CreateProps(
                executionId,
                definition,
                _pipeline,
                _timeProvider,
                envelope),
            actorName);
        Context.WatchWith(
            executionActor,
            new ReminderExecutionTerminated(executionId, definition.Id));

        _log.Info(
            "Started execution actor for reminder '{0}' occurrence={1}: {2}",
            definition.Id, envelope.DueTimeUtc, executionActor.Path);
    }

    private async Task<ScheduleAttempt> ScheduleDefinitionAsync(ReminderDefinition definition, bool rescheduleFromNow)
    {
        if (_client is null)
            return ScheduleAttempt.Fail("Reminder client is not initialized.");

        var id = definition.Id;
        var key = new ReminderKey(definition.Id.Value);
        var payload = new ReminderPayload { Id = id };
        var now = _timeProvider.GetUtcNow();

        try
        {
            switch (definition.Schedule.Type)
            {
                case ReminderScheduleType.OneShot:
                    {
                        if (definition.Schedule.FireAt is null)
                            return ScheduleAttempt.Fail("One-shot reminders require an absolute fire time.");

                        var fireAt = definition.Schedule.FireAt.Value;
                        if (fireAt <= now)
                            return ScheduleAttempt.Fail("One-shot fire time is in the past.");

                        var result = await _client.ScheduleSingleReminderAsync(key, fireAt, payload);
                        return result.ResponseCode == ReminderScheduleResponseCode.Success
                            ? ScheduleAttempt.Ok(fireAt)
                            : ScheduleAttempt.Fail(result.Message ?? "Failed to schedule one-shot reminder.");
                    }

                case ReminderScheduleType.Interval:
                    {
                        if (definition.Schedule.Interval is null)
                            return ScheduleAttempt.Fail("Interval reminders require an interval duration.");

                        var interval = definition.Schedule.Interval.Value;
                        var first = rescheduleFromNow
                            ? now.Add(interval)
                            : definition.Schedule.FireAt is { } explicitFirst && explicitFirst > now
                                ? explicitFirst
                                : now.Add(interval);

                        var result = await _client.ScheduleRecurringReminderAsync(key, first, interval, payload);
                        return result.ResponseCode == ReminderScheduleResponseCode.Success
                            ? ScheduleAttempt.Ok(first)
                            : ScheduleAttempt.Fail(result.Message ?? "Failed to schedule interval reminder.");
                    }

                case ReminderScheduleType.Cron:
                    {
                        if (string.IsNullOrWhiteSpace(definition.Schedule.CronExpression))
                            return ScheduleAttempt.Fail("Cron reminders require a cron expression.");

                        var nextFire = CronScheduleHelper.GetNextOccurrence(definition.Schedule.CronExpression, _timeProvider);
                        if (nextFire is null)
                            return ScheduleAttempt.Fail("Cron schedule has no future occurrence.");

                        var result = await _client.ScheduleSingleReminderAsync(key, nextFire.Value, payload);
                        return result.ResponseCode == ReminderScheduleResponseCode.Success
                            ? ScheduleAttempt.Ok(nextFire)
                            : ScheduleAttempt.Fail(result.Message ?? "Failed to schedule cron reminder.");
                    }

                default:
                    return ScheduleAttempt.Fail("Unknown schedule type.");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error scheduling reminder '{0}'", definition.Id);
            return ScheduleAttempt.Fail(ex.Message);
        }
    }

    private async Task<Dictionary<string, DateTimeOffset?>> ListScheduledRemindersAsync()
    {
        var map = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);

        if (_client is null)
            return map;

        var result = await _client.ListRemindersAsync();
        if (result.ResponseCode != FetchRemindersResponseCode.Success)
            return map;

        foreach (var scheduled in result.Reminders)
        {
            if (scheduled.Message is ReminderPayload payload)
                map[payload.Id.Value] = scheduled.When;
            // Ignore unknown payload types
        }

        return map;
    }

    private async Task<bool> CancelScheduleOnlyAsync(ReminderId id)
    {
        if (_client is null)
            return false;

        try
        {
            var result = await _client.CancelReminderAsync(new ReminderKey(id.Value));
            return result.ResponseCode == ReminderCancelResponseCode.Success;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error cancelling reminder schedule '{0}'", id.Value);
            return false;
        }
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]", RegexOptions.Compiled)]
    private static partial Regex InvalidActorNameChars();

    private static ReminderInfo ToReminderInfo(ReminderDefinition d, DateTimeOffset? nextFire) => new(
        Id: d.Id,
        Title: d.Title,
        Instructions: d.Instructions,
        Delivery: d.Delivery,
        DeliveryRequired: d.DeliveryRequired,
        DeliveryInstructions: d.DeliveryInstructions,
        Schedule: d.Schedule,
        NextFire: nextFire,
        Enabled: d.Enabled,
        AgentDefinitionId: d.AgentDefinitionId,
        Audience: d.Audience,
        ExpiresAt: d.ExpiresAt,
        ConsecutiveFailures: d.ConsecutiveFailures,
        TerminalOutcome: d.TerminalOutcome);

    private static string SanitizeActorName(string raw)
    {
        var sanitized = InvalidActorNameChars().Replace(raw, "-");
        if (string.IsNullOrWhiteSpace(sanitized))
            return "reminder";
        if (sanitized.Length > 60)
            return sanitized[..60];
        return sanitized;
    }

    private void HandleGetHealth()
    {
        var scheduledCount = _definitionStore.List().Count(d => d.Enabled);
        Sender.Tell(new ReminderHealthResponse(
            scheduledCount,
            _activeExecutions.Count,
            _definitionStore.List().Count(d => d.ConsecutiveFailures > 0)));
    }

    private async Task HandleGetStatusAsync(GetReminderStatusQuery query)
    {
        var replyTo = Sender;
        try
        {
            var definition = _definitionStore.Get(query.Id);
            if (definition is null)
            {
                replyTo.Tell(new ReminderStatusResponse(
                    query.Id, Found: false, Enabled: false, Executing: false,
                    NextFire: null, ConsecutiveFailures: 0, SkippedDuplicates: 0,
                    TerminalOutcome: null, Occurrence: null,
                    RecentHistory: []));
                return;
            }

            // Two independent backend reads — run them concurrently so the
            // query's latency is max(schedule, history) instead of their sum.
            // Neither touches actor state until both complete.
            var schedulesTask = ListScheduledRemindersAsync();
            var historyTask = _historyStore.ReadAsync(query.Id, RecentHistoryCount);
            var occurrenceTask = GetOccurrenceStatusAsync(definition);
            await Task.WhenAll(schedulesTask, historyTask, occurrenceTask);

            var occurrence = occurrenceTask.Result is { } status
                ? new ReminderOccurrenceInfo(
                    status.DueTimeUtc,
                    status.NextAttemptAtUtc,
                    status.AttemptCount,
                    status.LastFailureReason,
                    status.CompletionStatus.ToString(),
                    status.DeliveryDeadlineUtc,
                    status.AckDeadlineUtc,
                    status.CompletedAtUtc)
                : null;

            replyTo.Tell(new ReminderStatusResponse(
                query.Id,
                Found: true,
                Enabled: definition.Enabled,
                Executing: _activeExecutions.IsExecuting(query.Id),
                NextFire: schedulesTask.Result.GetValueOrDefault(query.Id.Value),
                ConsecutiveFailures: definition.ConsecutiveFailures,
                SkippedDuplicates: _skipCounts.GetValueOrDefault(query.Id),
                TerminalOutcome: definition.TerminalOutcome,
                Occurrence: occurrence,
                RecentHistory: historyTask.Result));
        }
        catch (Exception ex)
        {
            // The definition existed, so this is a transient read failure — NOT a
            // missing reminder. Faulting the Ask surfaces a real error (the
            // endpoint maps it to 5xx); replying not-found here would tell the
            // operator a wedged reminder was deleted, the silent fallback this
            // very feature exists to expose.
            _log.Error(ex, "Error getting status for reminder '{0}'", query.Id.Value);
            replyTo.Tell(new Status.Failure(ex));
        }
    }

    private async Task<ReminderOccurrenceStatus?> GetOccurrenceStatusAsync(ReminderDefinition definition)
    {
        if (_client is null
            || definition.Schedule.Type is not ReminderScheduleType.OneShot
            || definition.Schedule.FireAt is not { } dueTimeUtc)
        {
            return null;
        }

        var response = await _client.GetOccurrenceStatusAsync(
            new ReminderKey(definition.Id.Value),
            dueTimeUtc);

        return response.ResponseCode switch
        {
            ReminderOccurrenceStatusResponseCode.Success => response.Status,
            ReminderOccurrenceStatusResponseCode.NotFound => null,
            ReminderOccurrenceStatusResponseCode.Error => throw new InvalidOperationException(
                response.Message ?? "The reminder occurrence status query failed."),
            _ => throw new InvalidOperationException(
                $"Unexpected occurrence status response: {response.ResponseCode}.")
        };
    }

    private sealed record ScheduleAttempt(bool IsSuccess, DateTimeOffset? NextFire, string? ErrorMessage) : INoSerializationVerificationNeeded
    {
        public static ScheduleAttempt Ok(DateTimeOffset? nextFire) => new(true, nextFire, null);
        public static ScheduleAttempt Fail(string message) => new(false, null, message);
    }

    private sealed record ReminderAudienceAuthorizationResult(bool IsSuccess, TrustAudience? EffectiveAudience, string? ErrorMessage) : INoSerializationVerificationNeeded
    {
        public static ReminderAudienceAuthorizationResult Success(TrustAudience effectiveAudience)
            => new(true, effectiveAudience, null);

        public static ReminderAudienceAuthorizationResult Fail(string errorMessage)
            => new(false, null, errorMessage);
    }

    private sealed record ReminderBoundaryValidationResult(bool IsSuccess, TrustBoundary? NormalizedBoundary, string? ErrorMessage) : INoSerializationVerificationNeeded
    {
        public static ReminderBoundaryValidationResult Success(TrustBoundary normalizedBoundary)
            => new(true, normalizedBoundary, null);

        public static ReminderBoundaryValidationResult Fail(string errorMessage)
            => new(false, null, errorMessage);
    }

    internal sealed record ReconcileReminders : INoSerializationVerificationNeeded
    {
        public static readonly ReconcileReminders Instance = new();
    }

    /// <summary>
    /// Ack sent back to <see cref="ReconcileReminders"/> callers so they can
    /// synchronize on reconcile completion instead of polling.
    /// </summary>
    internal sealed record ReconcileCompleted(
        int CancelledOrphans,
        int RestoredSchedules,
        int SoftDeletedOneShots,
        int DisabledExpired = 0) : INoSerializationVerificationNeeded;
}
