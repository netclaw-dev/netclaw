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

    private readonly ISessionPipeline _pipeline;
    private readonly EffectivePolicyDefaults _defaults;
    private readonly SchedulingConfig _schedulingConfig;
    private readonly TimeProvider _timeProvider;
    private readonly ReminderDefinitionStore _definitionStore;
    private readonly ReminderHistoryStore _historyStore;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly ILoggingAdapter _log;

    private IReminderClient? _client;

    private readonly HashSet<Guid> _activeExecutionIds = [];
    private readonly Queue<ReminderId> _deferredQueue = new();
    private readonly Dictionary<ReminderId, int> _failureCounts = [];

    public ReminderManagerActor(
        ISessionPipeline pipeline,
        EffectivePolicyDefaults defaults,
        SchedulingConfig schedulingConfig,
        TimeProvider timeProvider,
        ReminderDefinitionStore definitionStore,
        ReminderHistoryStore historyStore,
        IOperationalNotificationSink notificationSink)
    {
        _pipeline = pipeline;
        _defaults = defaults;
        _schedulingConfig = schedulingConfig;
        _timeProvider = timeProvider;
        _definitionStore = definitionStore;
        _historyStore = historyStore;
        _notificationSink = notificationSink;
        _log = Context.GetLogger();

        ReceiveAsync<SaveReminderCommand>(HandleSaveAsync);
        ReceiveAsync<CancelReminderCommand>(HandleCancelAsync);
        ReceiveAsync<DisableReminderCommand>(HandleDisableAsync);
        ReceiveAsync<EnableReminderCommand>(HandleEnableAsync);
        ReceiveAsync<ListRemindersCommand>(HandleListAsync);
        ReceiveAsync<GetReminderCommand>(HandleGetAsync);

        ReceiveAsync<ReminderEnvelope<ReminderPayload>>(HandleReminderFiredAsync);
        ReceiveAsync<ReminderExecutionCompleted>(HandleExecutionCompletedAsync);

        ReceiveAsync<ReconcileReminders>(_ => HandleReconcileAsync());
        Receive<GetReminderHealthQuery>(_ => HandleGetHealth());
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

        EmitDroppedInvalidDefinitionAlerts();

        Self.Tell(ReconcileReminders.Instance);
    }

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
                new ReminderId(cmd.Definition.Id ?? "unknown"),
                string.Empty,
                Success: false,
                NextFire: null,
                Error: ReminderSaveError.Validation,
                ErrorMessage: "Reminder title is required."));
            return;
        }

        var id = !string.IsNullOrWhiteSpace(cmd.Definition.Id)
            ? new ReminderId(cmd.Definition.Id)
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

        var effectiveAudience = authorization.EffectiveAudience ?? TrustAudience.Public;
        var effectiveBoundary = ResolveReminderBoundary(
            cmd.Definition.Boundary,
            cmd.Definition.Delivery.OriginChannelType,
            effectiveAudience);

        var normalized = cmd.Definition with
        {
            Id = id.Value,
            Title = title,
            Audience = authorization.EffectiveAudience,
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
            RemoveFromDeferredQueue(id);
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
        }
        else
        {
            await CancelScheduleOnlyAsync(id);
            RemoveFromDeferredQueue(id);
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
        TrustAudience? requestedAudience,
        ReminderAudienceAuthorizationContext? authorization)
    {
        if (authorization?.SourceAudience is not { } sourceAudience)
        {
            return ReminderAudienceAuthorizationResult.Fail(
                "Reminder audience authorization context is required.");
        }

        var effectiveAudience = requestedAudience ?? sourceAudience;
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

    private static string ResolveReminderBoundary(
        string? requestedBoundary,
        ChannelType? originChannelType,
        TrustAudience effectiveAudience)
    {
        var channelType = (originChannelType ?? ChannelType.Reminder).ToWireValue();
        return SecurityPolicyDefaults.ResolveBoundary(requestedBoundary, channelType, effectiveAudience);
    }

    private async Task HandleCancelAsync(CancelReminderCommand cmd)
    {
        var replyTo = Sender;
        var deleted = _definitionStore.Delete(cmd.Id);
        var scheduleCancelled = await CancelScheduleOnlyAsync(cmd.Id);

        _failureCounts.Remove(cmd.Id);
        RemoveFromDeferredQueue(cmd.Id);

        if (deleted)
        {
            try
            {
                _historyStore.DeleteHistory(cmd.Id);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to delete history file for reminder '{0}'", cmd.Id.Value);
            }
        }

        var found = deleted || scheduleCancelled;
        _log.Info("Delete reminder '{0}': {1}", cmd.Id.Value, found ? "deleted" : "not found");
        replyTo.Tell(new ReminderCancelledResponse(cmd.Id, found));
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

        _failureCounts.Remove(id);
        RemoveFromDeferredQueue(id);

        _log.Info("Disabled reminder '{0}'", id.Value);
        return new ReminderStateResponse(id, Found: true, Enabled: false);
    }

    /// <summary>
    /// Permanently removes a reminder definition, its schedule, history, and any
    /// in-memory tracking state. Used during startup reconciliation to clean up
    /// stale one-shot reminders whose fire time has passed.
    /// </summary>
    private async Task DeleteReminderInternalAsync(ReminderId id)
    {
        _definitionStore.Delete(id);
        await CancelScheduleOnlyAsync(id);
        _failureCounts.Remove(id);
        RemoveFromDeferredQueue(id);

        try
        {
            _historyStore.DeleteHistory(id);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to delete history for reminder '{0}'", id.Value);
        }
    }

    private async Task<ReminderStateResponse> EnableReminderInternalAsync(ReminderId id)
    {
        var definition = _definitionStore.Get(id);
        if (definition is null)
            return new ReminderStateResponse(id, Found: false, Enabled: false, ErrorMessage: "Reminder not found.");

        definition = definition with
        {
            Enabled = true,
            UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        var scheduleResult = await ScheduleDefinitionAsync(definition, rescheduleFromNow: true);
        if (!scheduleResult.IsSuccess)
        {
            definition = definition with
            {
                Enabled = false,
                UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            };
            _definitionStore.Save(definition);

            return new ReminderStateResponse(
                id,
                Found: true,
                Enabled: false,
                ErrorMessage: scheduleResult.ErrorMessage);
        }

        _definitionStore.Save(definition);
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
                .Select(d => ToReminderInfo(d, schedules.GetValueOrDefault(d.Id)))
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
            var info = ToReminderInfo(definition, schedules.GetValueOrDefault(definition.Id));

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
            RemoveFromDeferredQueue(reminderId);
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

        var isCurrentSessionDelivery = definition.Delivery.Kind == DeliveryKind.CurrentSession;

        if (_activeExecutionIds.Count >= MaxConcurrentExecutions)
        {
            _log.Info("Concurrency limit reached ({0}), deferring reminder '{1}'",
                MaxConcurrentExecutions, reminderId.Value);
            _deferredQueue.Enqueue(reminderId);
            // Ack even Mode B envelopes on the deferred path — the
            // concurrency gate fires before we can dispatch to the
            // gateway, so holding the envelope open would starve
            // Akka.Reminders' retry budget on nothing.
            await _client!.AckAsync(envelope);
            return;
        }

        if (isCurrentSessionDelivery)
        {
            // CurrentSession: execution actor holds the envelope open and acks
            // itself once the target session has confirmed receipt.
            StartExecution(definition, envelope);
        }
        else
        {
            // Channel/None: ack envelope eagerly, execution tracks its own success
            StartExecution(definition);
            await _client!.AckAsync(envelope);
        }
    }

    private async Task HandleExecutionCompletedAsync(ReminderExecutionCompleted completed)
    {
        if (!_activeExecutionIds.Remove(completed.ExecutionId))
            return;

        var definition = _definitionStore.Get(completed.Id);
        var title = definition?.Title ?? completed.Id.Value;

        if (completed.Success)
        {
            _failureCounts.Remove(completed.Id);
            _log.Info("Reminder '{0}' execution completed successfully", completed.Id.Value);
        }
        else
        {
            var count = _failureCounts.GetValueOrDefault(completed.Id) + 1;
            _failureCounts[completed.Id] = count;

            _log.Warning("Reminder '{0}' execution failed ({1}/{2}): {3}",
                completed.Id.Value,
                count,
                FailurePauseThreshold,
                completed.ErrorMessage);

            _notificationSink.Emit(OperationalAlert.Create(
                _timeProvider,
                "reminder.execution.failed",
                AlertType.ReminderExecutionFailed,
                $"Reminder '{title}' execution failed: {completed.ErrorMessage}",
                AlertSeverity.Warning,
                source: completed.Id.Value,
                context: new Dictionary<string, string>
                {
                    ["reminderId"] = completed.Id.Value,
                    ["title"] = title,
                    ["error"] = completed.ErrorMessage ?? "unknown",
                }));

            if (count >= FailurePauseThreshold)
            {
                _log.Warning("Reminder '{0}' hit failure threshold ({1}), disabling",
                    completed.Id.Value,
                    FailurePauseThreshold);

                _notificationSink.Emit(OperationalAlert.Create(
                    _timeProvider,
                    "reminder.auto_disabled",
                    AlertType.ReminderAutoDisabled,
                    $"Reminder '{title}' disabled after {count} consecutive failures",
                    AlertSeverity.Critical,
                    source: completed.Id.Value,
                    context: new Dictionary<string, string>
                    {
                        ["reminderId"] = completed.Id.Value,
                        ["title"] = title,
                        ["failureCount"] = count.ToString(),
                    }));

                await DisableReminderInternalAsync(completed.Id);
                _failureCounts.Remove(completed.Id);
            }
        }

        // One-shot reminders cannot fire again — soft-delete by disabling.
        // The definition stays on disk so history remains queryable.
        // Startup reconciliation will hard-delete stale disabled one-shots.
        if (definition is { Schedule.Type: ReminderScheduleType.OneShot })
        {
            _log.Info("One-shot reminder '{0}' completed, disabling (soft-delete)", completed.Id.Value);
            await DisableReminderInternalAsync(completed.Id);
        }

        await ProcessDeferredQueueAsync();
    }

    private async Task HandleReconcileAsync()
    {
        var sender = Sender; // capture before any await
        try
        {
            var scheduled = await ListScheduledRemindersAsync();
            var definitions = _definitionStore.List();
            var definitionsById = definitions.ToDictionary(d => d.Id, StringComparer.Ordinal);

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
                if (scheduled.ContainsKey(definition.Id))
                    continue;

                var result = await ScheduleDefinitionAsync(definition, rescheduleFromNow: true);
                if (result.IsSuccess)
                    restoredSchedules++;
            }

            // Delete stale one-shots: definitions with fire time in the past
            // and no active Akka.Reminders schedule (already fired, never cleaned up).
            // Includes both enabled zombies and already-disabled leftovers.
            var now = _timeProvider.GetUtcNow();
            var deletedOneShots = 0;
            foreach (var definition in definitions.Where(d =>
                         d.Schedule.Type == ReminderScheduleType.OneShot &&
                         d.Schedule.FireAt <= now &&
                         !scheduled.ContainsKey(d.Id)))
            {
                await DeleteReminderInternalAsync(new ReminderId(definition.Id));
                deletedOneShots++;
            }

            // Disable expired recurring reminders that haven't fired since expiration.
            var disabledExpired = 0;
            foreach (var definition in definitions.Where(d =>
                         d.Enabled &&
                         d.Schedule.Type is not ReminderScheduleType.OneShot &&
                         d.ExpiresAt is { } ea && ea <= now))
            {
                await DisableReminderInternalAsync(new ReminderId(definition.Id));
                disabledExpired++;
            }

            if (cancelledOrphans > 0 || restoredSchedules > 0 || deletedOneShots > 0 || disabledExpired > 0)
            {
                _log.Info("Reminder reconcile complete: cancelled_orphans={0}, restored={1}, deleted_oneshots={2}, disabled_expired={3}",
                    cancelledOrphans,
                    restoredSchedules,
                    deletedOneShots,
                    disabledExpired);
            }

            // Only ack external callers — skip Self.Tell from PreStart
            if (!sender.Equals(Self))
                sender.Tell(new ReconcileCompleted(cancelledOrphans, restoredSchedules, deletedOneShots, disabledExpired));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Reminder reconcile failed");

            // Reply with a zero-result ack so external Ask callers don't hang until timeout
            if (!sender.Equals(Self))
                sender.Tell(new ReconcileCompleted(0, 0, 0));
        }
    }

    private void StartExecution(ReminderDefinition definition, ReminderEnvelope<ReminderPayload>? envelope = null)
    {
        var executionId = Guid.NewGuid();
        _activeExecutionIds.Add(executionId);

        var actorName = $"exec-{SanitizeActorName(definition.Id)}-{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}";
        var executionActor = Context.ActorOf(
            ReminderExecutionActor.CreateProps(
                executionId,
                definition,
                _pipeline,
                _timeProvider,
                _historyStore,
                envelope),
            actorName);

        _log.Info(
            "Started execution actor for reminder '{0}' mode={1}: {2}",
            definition.Id, envelope is null ? "A" : "B", executionActor.Path);
    }

    private async Task ProcessDeferredQueueAsync()
    {
        while (_deferredQueue.Count > 0 && _activeExecutionIds.Count < MaxConcurrentExecutions)
        {
            var nextId = _deferredQueue.Dequeue();
            var definition = _definitionStore.Get(nextId);
            if (definition is null || !definition.Enabled)
                continue;

            var now = _timeProvider.GetUtcNow();
            if (definition.Schedule.Type is not ReminderScheduleType.OneShot
                && definition.ExpiresAt is { } expiresAt
                && expiresAt <= now)
            {
                _log.Info("Deferred reminder '{0}' expired while queued (expiresAt={1}), disabling", nextId.Value, expiresAt);
                await DisableReminderInternalAsync(nextId);
                continue;
            }

            StartExecution(definition);
        }
    }

    private void RemoveFromDeferredQueue(ReminderId id)
    {
        if (_deferredQueue.Count == 0)
            return;

        var keep = new Queue<ReminderId>();
        while (_deferredQueue.Count > 0)
        {
            var item = _deferredQueue.Dequeue();
            if (item != id)
                keep.Enqueue(item);
        }

        while (keep.Count > 0)
            _deferredQueue.Enqueue(keep.Dequeue());
    }

    private async Task<ScheduleAttempt> ScheduleDefinitionAsync(ReminderDefinition definition, bool rescheduleFromNow)
    {
        if (_client is null)
            return ScheduleAttempt.Fail("Reminder client is not initialized.");

        var id = new ReminderId(definition.Id);
        var key = new ReminderKey(definition.Id);
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

                    definition.Schedule.FireAt = first;

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
        Id: new ReminderId(d.Id),
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
        ExpiresAt: d.ExpiresAt);

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
            _activeExecutionIds.Count,
            _failureCounts.Count));
    }

    private sealed record ScheduleAttempt(bool IsSuccess, DateTimeOffset? NextFire, string? ErrorMessage)
    {
        public static ScheduleAttempt Ok(DateTimeOffset? nextFire) => new(true, nextFire, null);
        public static ScheduleAttempt Fail(string message) => new(false, null, message);
    }

    private sealed record ReminderAudienceAuthorizationResult(bool IsSuccess, TrustAudience? EffectiveAudience, string? ErrorMessage)
    {
        public static ReminderAudienceAuthorizationResult Success(TrustAudience effectiveAudience)
            => new(true, effectiveAudience, null);

        public static ReminderAudienceAuthorizationResult Fail(string errorMessage)
            => new(false, null, errorMessage);
    }

    internal sealed record ReconcileReminders
    {
        public static readonly ReconcileReminders Instance = new();
    }

    /// <summary>
    /// Ack sent back to <see cref="ReconcileReminders"/> callers so they can
    /// synchronize on reconcile completion instead of polling.
    /// </summary>
    internal sealed record ReconcileCompleted(int CancelledOrphans, int RestoredSchedules, int DeletedOneShots, int DisabledExpired = 0);
}
