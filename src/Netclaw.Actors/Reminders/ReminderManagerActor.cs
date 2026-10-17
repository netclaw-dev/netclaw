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
public sealed partial class ReminderManagerActor : ReceiveActor, IWithTimers
{
    public const string ShardRegionName = "netclaw-reminders";
    public const string EntityId = "manager";

    /// <summary>
    /// Consecutive execution failures after which a reminder is auto-paused.
    /// Not configurable. Must stay strictly below Akka.Reminders'
    /// <c>MaxDeliveryAttempts</c> (default 10) so Netclaw's auto-pause fires
    /// first — the two counters are kept out of conflict by inspection.
    /// </summary>
    internal const int FailurePauseThreshold = 5;

    /// <summary>
    /// How long the tombstone and the execution history of an ended one-shot stay before the sweep
    /// deletes them. Not configurable. Also handed to Akka.Reminders as its
    /// <c>PruneOlderThan</c> (the library's own default), so both stores agree.
    /// </summary>
    internal static readonly TimeSpan TerminalRetention = TimeSpan.FromDays(12);

    /// <summary>How often the sweep ends leftover one-shots and purges tombstones past <see cref="TerminalRetention"/>.</summary>
    internal static readonly TimeSpan TerminalPruneInterval = TimeSpan.FromHours(12);

    internal static readonly object TerminalPruneTimerKey = new();

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

    /// <summary>Timer scheduler injected by Akka for <see cref="IWithTimers"/>.</summary>
    public ITimerScheduler Timers { get; set; } = null!;

    private readonly ActiveExecutionTracker _activeExecutions = new();
    private readonly Dictionary<ReminderId, int> _skipCounts = [];
    // The denial text last alerted per reminder; cleared by its next ok run, so a reminder that is denied on
    // every run alerts once. Not persisted: a restart may alert once more.
    private readonly Dictionary<ReminderId, string> _deniedAlerts = [];

    // Uniqueness source for execution child actor names. A wall-clock
    // millisecond suffix collided when two fires for one reminder landed in
    // the same millisecond and threw InvalidActorNameException. The actor is
    // single-threaded, so a plain counter is collision-free for its lifetime.
    private long _executionSequence;

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
        ReceiveAsync<GetReminderHistoryQuery>(HandleGetHistoryAsync);

        ReceiveAsync<ReminderEnvelope<ReminderPayload>>(HandleReminderFiredAsync);
        ReceiveAsync<ReminderExecutionCompleted>(HandleExecutionOutcomeAsync);
        ReceiveAsync<ReminderExecutionTerminated>(HandleExecutionTerminatedAsync);

        ReceiveAsync<ReconcileReminders>(_ => HandleReconcileAsync());
        ReceiveAsync<PruneTerminalReminders>(_ => SweepEndedOneShotsAsync());
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
        Timers.StartPeriodicTimer(TerminalPruneTimerKey, PruneTerminalReminders.Instance, TerminalPruneInterval);
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
        var existing = exists ? _definitionStore.Get(id) : null;
        var tombstone = ReadTombstone(id);

        // The id collides with a reminder the caller cannot see. Reject the save
        // outright — the same "not found" shape a genuinely missing id gets — for
        // every write mode. Letting CreateOnly report Conflict would disclose that
        // the id exists; letting Replace/Upsert proceed would silently overwrite a
        // reminder outside the caller's audience.
        if (existing is not null && !CanAccessAudience(cmd.Authorization, existing.Audience))
        {
            LogAudienceDenied("save", id, existing.Audience, cmd.Authorization);
            replyTo.Tell(new ReminderSavedResponse(
                id,
                title,
                Success: false,
                NextFire: null,
                Error: ReminderSaveError.NotFound,
                ErrorMessage: $"Reminder '{id.Value}' was not found."));
            return;
        }

        // The id of an ended one-shot outside the caller's audience is blocked until the sweep purges its
        // tombstone. The reply is the same "not found" as for a definition outside the audience.
        if (existing is null && tombstone is not null && !CanAccessAudience(cmd.Authorization, tombstone.Audience))
        {
            LogAudienceDenied("save", id, tombstone.Audience, cmd.Authorization);
            replyTo.Tell(new ReminderSavedResponse(
                id,
                title,
                Success: false,
                NextFire: null,
                Error: ReminderSaveError.NotFound,
                ErrorMessage: $"Reminder '{id.Value}' was not found."));
            return;
        }

        switch (cmd.WriteMode)
        {
            // The id of an ended one-shot is free again: only a definition makes an id exist.
            case ReminderWriteMode.CreateOnly when exists:
                replyTo.Tell(new ReminderSavedResponse(
                    id,
                    title,
                    Success: false,
                    NextFire: null,
                    Error: ReminderSaveError.Conflict,
                    ErrorMessage: $"Reminder '{id.Value}' already exists."));
                return;

            case ReminderWriteMode.Replace when !exists && tombstone is null:
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

        if (exists)
        {
            normalized.CreatedAtMs = existing?.CreatedAtMs ?? (normalized.CreatedAtMs > 0 ? normalized.CreatedAtMs : now.ToUnixTimeMilliseconds());
        }
        else
        {
            normalized.CreatedAtMs = normalized.CreatedAtMs > 0 ? normalized.CreatedAtMs : now.ToUnixTimeMilliseconds();
        }

        normalized.UpdatedAtMs = now.ToUnixTimeMilliseconds();

        if (exists)
            await CancelScheduleOnlyAsync(id);

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

        // A re-arm of an ended id starts a new reminder. The schedule came first, so a failed schedule
        // keeps the old data. The history goes first: the tombstone is its only audience anchor, and a
        // new reminder can have a lower audience than the old one. A tombstone file that Netclaw cannot
        // read counts too, because the history behind it has no audience to check.
        if (_definitionStore.TombstoneExists(id))
        {
            _historyStore.DeleteHistory(id);
            _definitionStore.DeleteTombstone(id);
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
        var target = _definitionStore.Get(cmd.Id);
        if (target is null)
        {
            // An ended one-shot has only a tombstone. A cancel of it changes nothing.
            var endedTombstone = ReadTombstone(cmd.Id);
            if (endedTombstone is not null && CanAccessAudience(cmd.Authorization, endedTombstone.Audience))
            {
                _log.Info("Cancel reminder '{0}': already ended ({1})", cmd.Id.Value, endedTombstone.Outcome);
                replyTo.Tell(new ReminderCancelledResponse(
                    cmd.Id, Found: true, AlreadyEnded: ToEndedInfo(endedTombstone, includeInstructions: false)));
                return;
            }

            if (endedTombstone is not null)
                LogAudienceDenied("cancel", cmd.Id, endedTombstone.Audience, cmd.Authorization);

            _log.Info("Cancel reminder '{0}': not found", cmd.Id.Value);
            replyTo.Tell(new ReminderCancelledResponse(cmd.Id, Found: false));
            return;
        }

        if (!CanAccessAudience(cmd.Authorization, target.Audience))
        {
            LogAudienceDenied("cancel", cmd.Id, target.Audience, cmd.Authorization);
            _log.Info("Cancel reminder '{0}': not found", cmd.Id.Value);
            replyTo.Tell(new ReminderCancelledResponse(cmd.Id, Found: false));
            return;
        }

        if (target.Schedule.Type == ReminderScheduleType.OneShot)
        {
            replyTo.Tell(await CancelOneShotAsync(target));
            return;
        }

        var response = await DisableReminderInternalAsync(cmd.Id);

        _log.Info("Cancel reminder '{0}': {1}", cmd.Id.Value, response.Found ? "disabled" : "not found");
        replyTo.Tell(new ReminderCancelledResponse(cmd.Id, response.Found));
    }

    /// <summary>
    /// A cancel ends a one-shot with the outcome <c>Cancelled</c>. A run that is active continues, and its
    /// settlement keeps the tombstone (design D5). A matching tombstone means the one-shot already ended in
    /// a crash window, so the cancel finishes that end and changes no outcome.
    /// </summary>
    private async Task<ReminderCancelledResponse> CancelOneShotAsync(ReminderDefinition target)
    {
        var existingTombstone = ReadTombstone(target.Id);
        if (existingTombstone is not null && TombstoneMatches(existingTombstone, target))
        {
            await FinishEndedOneShotAsync(target, envelope: null, EnvelopeAction.None, nackReason: null);
            _log.Info("Cancel reminder '{0}': already ended ({1})", target.Id.Value, existingTombstone.Outcome);
            return new ReminderCancelledResponse(
                target.Id, Found: true, AlreadyEnded: ToEndedInfo(existingTombstone, includeInstructions: false));
        }

        var ended = await EndOneShotAsync(
            target,
            ReminderTerminalOutcome.Cancelled,
            lastError: null,
            _timeProvider.GetUtcNow(),
            envelope: null,
            EnvelopeAction.None);
        if (!ended)
        {
            return new ReminderCancelledResponse(
                target.Id,
                Found: true,
                ErrorMessage: $"Reminder '{target.Id.Value}' was not cancelled. Netclaw could not write the end record. The reminder did not change.");
        }

        _log.Info("Cancel reminder '{0}': cancelled and deleted", target.Id.Value);
        return new ReminderCancelledResponse(target.Id, Found: true, Deleted: true);
    }

    private async Task HandlePermanentDeleteAsync(DeleteReminderCommand cmd)
    {
        var replyTo = Sender;
        var found = _definitionStore.Exists(cmd.Id) || _definitionStore.TombstoneExists(cmd.Id);
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
            return NotFoundOrEndedState(id);

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
    /// Permanently removes a reminder definition, its schedule, history, tombstone, and process state.
    /// Only an explicit delete command uses this path.
    /// </summary>
    private async Task DeleteReminderInternalAsync(ReminderId id)
    {
        await CancelScheduleOnlyAsync(id);
        _skipCounts.Remove(id);
        _deniedAlerts.Remove(id);

        try
        {
            _historyStore.DeleteHistory(id);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to delete history for reminder '{0}'", id.Value);
            throw;
        }

        // The history is gone, so the tombstone has nothing left to anchor. Delete it before the
        // definition so reconciliation can retry a partial cleanup.
        _definitionStore.DeleteTombstone(id);

        // Delete the definition last so reconciliation can retry a partial cleanup.
        _definitionStore.Delete(id);
    }

    /// <summary>
    /// The reply of enable and disable for an id without a definition. An ended one-shot has no definition to
    /// change, and the reply says so. These commands are operator-only, so no audience gate applies.
    /// </summary>
    private ReminderStateResponse NotFoundOrEndedState(ReminderId id)
    {
        var tombstone = ReadTombstone(id);
        return new ReminderStateResponse(
            id,
            Found: false,
            Enabled: false,
            ErrorMessage: tombstone is null
                ? "Reminder not found."
                : ToEndedInfo(tombstone, includeInstructions: false).DescribeEnd(id));
    }

    private async Task<ReminderStateResponse> EnableReminderInternalAsync(ReminderId id)
    {
        var definition = _definitionStore.Get(id);
        if (definition is null)
            return NotFoundOrEndedState(id);

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

            // Out-of-scope reminders are omitted, never flagged — a caller must not
            // be able to tell an invisible reminder apart from one that does not
            // exist. No logging here: List is not a per-id lookup, so there is no
            // single denial to record.
            var infos = definitions
                .Where(d => cmd.IncludeDisabled || d.Enabled)
                .Where(d => CanAccessAudience(cmd.Authorization, d.Audience))
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
                // The prompt of a Failed tombstone has one read surface: status. A get reply never holds it.
                var ended = ReadTombstone(cmd.Id);
                if (ended is not null && CanAccessAudience(cmd.Authorization, ended.Audience))
                {
                    replyTo.Tell(new GetReminderResponse(null, ToEndedInfo(ended, includeInstructions: false)));
                    return;
                }

                if (ended is not null)
                    LogAudienceDenied("get", cmd.Id, ended.Audience, cmd.Authorization);

                replyTo.Tell(new GetReminderResponse(null));
                return;
            }

            if (!CanAccessAudience(cmd.Authorization, definition.Audience))
            {
                LogAudienceDenied("get", cmd.Id, definition.Audience, cmd.Authorization);
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
            // A fire after the end is normal after a crash: the end stopped before it cancelled the
            // schedule, or a re-arm stopped after it scheduled the new occurrence (design D4, D6).
            if (ReadTombstone(reminderId) is not null)
            {
                _log.Info(
                    "Reminder '{0}' fired after it ended. Cancelling the schedule and acknowledging the occurrence.",
                    reminderId.Value);
            }
            else
            {
                _log.Error("Reminder fired for missing definition '{0}'. Cancelling orphaned schedule.", reminderId.Value);
            }

            await CancelScheduleOnlyAsync(reminderId);
            await _client!.AckAsync(envelope);
            return;
        }

        // A matching tombstone means the one-shot already ended, so the fire runs nothing (design D4).
        if (definition.Schedule.Type == ReminderScheduleType.OneShot
            && await TryFinishMatchedTombstoneAsync(definition, envelope))
        {
            return;
        }

        if (!definition.Enabled)
        {
            _log.Warning("Reminder '{0}' fired while disabled. Cancelling any lingering schedule.", reminderId.Value);
            await CancelScheduleOnlyAsync(reminderId);
            await _client!.AckAsync(envelope);
            return;
        }

        if (definition.ExpiresAt is { } expiresAt
            && expiresAt <= _timeProvider.GetUtcNow())
        {
            if (definition.Schedule.Type == ReminderScheduleType.OneShot)
            {
                // The tombstone comes before the ack. If the ack came first and the process stopped, the
                // reconciliation would read Delivered and record Completed for a reminder that did not run.
                _log.Info("Reminder '{0}' has expired (expiresAt={1}), ending it", reminderId.Value, expiresAt);
                await EndOneShotAsync(
                    definition,
                    ReminderTerminalOutcome.Expired,
                    $"The reminder expired at {expiresAt:u} before it fired.",
                    _timeProvider.GetUtcNow(),
                    envelope,
                    EnvelopeAction.Ack);
                return;
            }

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
                // Surface the failure through the shared failure path instead of only
                // logging. The current occurrence still executes below; only the next
                // occurrence is lost, and that loss is now alerted and counted.
                await ReportScheduleFailureAsync(
                    definition,
                    scheduleResult.ErrorMessage ?? "Failed to reschedule cron reminder.");
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

        if (!HasSafeExecutionLease(definition, envelope, _timeProvider.GetUtcNow()))
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
        ReminderDefinition definition,
        ReminderEnvelope<ReminderPayload> envelope,
        DateTimeOffset now)
    {
        if (envelope.Deadline.IsInfinite)
            return true;

        var remaining = envelope.Deadline.UtcDateTime - now;
        var requiredLease = ReminderExecutionActor.ExecutionAttemptTimeout + SettlementMargin;
        if (remaining >= requiredLease)
            return true;

        // An interval shorter than the required lease can never satisfy it. The
        // deadline of each of its occurrences is the next due time, and
        // Akka.Reminders does not redeliver an occurrence after that time. The
        // occurrence starts while it is current. An occurrence that arrives at
        // or after its next due time is stale.
        return definition.Schedule is { Type: ReminderScheduleType.Interval, Interval: { } interval }
            && interval < requiredLease
            && remaining > TimeSpan.Zero;
    }

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
                // NotFound means the occurrence is no longer awaiting ack (already
                // settled by another client or a timeout). That is an idempotent
                // no-op, not a settlement failure — only Error is a real failure.
                if (response.ResponseCode is ReminderNackResponseCode.Error)
                {
                    EmitSettlementFailure(
                        definition,
                        $"Negative acknowledgement returned {response.ResponseCode}: {response.Message}");
                }

                return;
            }

            var ack = await _client!.AckAsync(envelope);
            // NotFound is a duplicate ack of an already-settled occurrence —
            // idempotent no-op, not a failure.
            if (ack.ResponseCode is ReminderAckResponseCode.Error)
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

        // Design D5: a settlement ends only the occurrence that it ran. A definition that another save
        // replaced during the run, or that a cancel deleted, is not this occurrence's to change.
        var endsOneShot = definition is { Schedule.Type: ReminderScheduleType.OneShot }
                          && SettledOccurrenceIsCurrent(definition, execution.Envelope);
        var supersededOneShot = definition is { Schedule.Type: ReminderScheduleType.OneShot } && !endsOneShot;

        if (outcome.Success && outcome.History.ToolDenied)
        {
            await SettleDeniedExecutionAsync(outcome, execution, definition, endsOneShot);
            return;
        }

        if (supersededOneShot)
        {
            await SettleSupersededOccurrenceAsync(outcome, execution, definition!);
            return;
        }

        if (outcome.Success)
        {
            _deniedAlerts.Remove(outcome.Id);
            await SettleSuccessfulExecutionAsync(outcome, execution, definition, endsOneShot);
            return;
        }

        await SettleFailedExecutionAsync(outcome, execution, definition, endsOneShot);
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

    /// <summary>
    /// A run whose one-shot definition another save replaced during the run. The history row is already
    /// written. The manager acknowledges the old occurrence and leaves the new definition and its
    /// schedule alone. A retry of the old occurrence would run the new definition early.
    /// </summary>
    private async Task SettleSupersededOccurrenceAsync(
        ReminderExecutionCompleted outcome,
        ActiveReminderExecution execution,
        ReminderDefinition definition)
    {
        _log.Warning(
            "Reminder '{0}' was set again during a run of the occurrence due {1:u}. The run ended with success={2}. The new definition and its schedule stay.",
            outcome.Id.Value, execution.Envelope.DueTimeUtc, outcome.Success);

        // The run failed even though the definition is new. The owner still gets the failure alert, but the
        // failure does not count against the new definition.
        if (!outcome.Success)
        {
            ReportExecutionFailure(
                outcome.Id,
                definition,
                count: 1,
                string.IsNullOrWhiteSpace(outcome.ErrorMessage) ? "Reminder execution failed." : outcome.ErrorMessage,
                willDisable: false);
        }

        try
        {
            var ack = await _client!.AckAsync(execution.Envelope);
            if (ack.ResponseCode is ReminderAckResponseCode.Error)
                EmitSettlementFailure(definition, ack.Message ?? $"Reminder acknowledgement returned {ack.ResponseCode}.");
        }
        catch (Exception ex)
        {
            EmitSettlementFailure(definition, ex.Message, ex);
        }
    }

    /// <summary>
    /// A run in which authorization denied a tool call. The denial is deterministic, so the occurrence is
    /// acknowledged: no retry, no failure count, no auto-disable, and a recurring reminder keeps its
    /// schedule. The owner gets one alert per distinct denial until the reminder next records an ok run.
    /// A one-shot ends as <c>Failed</c>: its tombstone keeps the denial text and the prompt, and its history
    /// keeps the <c>denied</c> row.
    /// </summary>
    private async Task SettleDeniedExecutionAsync(
        ReminderExecutionCompleted outcome,
        ActiveReminderExecution execution,
        ReminderDefinition? definition,
        bool endsOneShot)
    {
        var reason = outcome.ErrorMessage ?? "A tool call was denied.";
        _log.Warning("Reminder '{0}' ran with a denied tool call: {1}", outcome.Id.Value, reason);

        if (!_deniedAlerts.TryGetValue(outcome.Id, out var alerted) || alerted != reason)
        {
            _deniedAlerts[outcome.Id] = reason;
            var title = definition?.Title ?? outcome.Id.Value;
            _notificationSink.Emit(OperationalAlert.Create(
                _timeProvider,
                "reminder.tool_denied",
                AlertType.ReminderExecutionFailed,
                $"Reminder '{title}' was denied a tool: {reason}",
                AlertSeverity.Warning,
                source: outcome.Id.Value,
                context: new Dictionary<string, string>
                {
                    ["reminderId"] = outcome.Id.Value,
                    ["title"] = title,
                    ["error"] = reason
                }));
        }

        if (endsOneShot)
        {
            await EndOneShotAsync(
                definition!,
                ReminderTerminalOutcome.Failed,
                reason,
                _timeProvider.GetUtcNow(),
                execution.Envelope,
                EnvelopeAction.Ack);
            return;
        }

        try
        {
            var ack = await _client!.AckAsync(execution.Envelope);
            if (ack.ResponseCode is ReminderAckResponseCode.Error && definition is not null)
                EmitSettlementFailure(definition, ack.Message ?? $"Reminder acknowledgement returned {ack.ResponseCode}.");
        }
        catch (Exception ex)
        {
            if (definition is not null)
                EmitSettlementFailure(definition, ex.Message, ex);
        }
    }

    private async Task SettleSuccessfulExecutionAsync(
        ReminderExecutionCompleted outcome,
        ActiveReminderExecution execution,
        ReminderDefinition? definition,
        bool endsOneShot)
    {
        if (endsOneShot)
        {
            // The tombstone comes before the ack (design D3). If the ack fails, the matching tombstone
            // stops the redelivery from running the reminder again.
            if (await EndOneShotAsync(
                    definition!,
                    ReminderTerminalOutcome.Completed,
                    lastError: null,
                    _timeProvider.GetUtcNow(),
                    execution.Envelope,
                    EnvelopeAction.Ack))
            {
                _log.Info("Reminder '{0}' execution completed successfully", outcome.Id.Value);
            }

            return;
        }

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

        if (ack.ResponseCode is ReminderAckResponseCode.Error)
        {
            if (definition is not null)
            {
                EmitSettlementFailure(
                    definition,
                    ack.Message ?? $"Reminder acknowledgement returned {ack.ResponseCode}.");
            }

            return;
        }

        _log.Info("Reminder '{0}' execution completed successfully", outcome.Id.Value);
    }

    private async Task SettleFailedExecutionAsync(
        ReminderExecutionCompleted outcome,
        ActiveReminderExecution execution,
        ReminderDefinition? definition,
        bool endsOneShot)
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
                // A one-shot that reaches the threshold ends into a tombstone below. It is not saved
                // as a disabled definition, because its fire time is in the past and enable could never run it.
                var disables = thresholdReached && !endsOneShot;
                definition = definition with
                {
                    ConsecutiveFailures = count,
                    Enabled = disables ? false : definition.Enabled,
                    TerminalOutcome = disables ? ReminderTerminalOutcome.Failed : definition.TerminalOutcome,
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

        if (endsOneShot && thresholdReached)
        {
            // The tombstone comes first, then the nack, the schedule cancel, and the delete (design D3).
            // If the tombstone cannot be written, no nack goes out. The lease expiry redelivers the
            // occurrence, and the next failure tries again.
            var ended = await EndOneShotAsync(
                definition!,
                ReminderTerminalOutcome.Failed,
                reason,
                _timeProvider.GetUtcNow(),
                execution.Envelope,
                EnvelopeAction.Nack);
            ReportExecutionFailure(outcome.Id, definition, count, reason, willDisable: ended);
            return;
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

        // An expired occurrence ends a one-shot. It does not end a reminder
        // series: Akka.Reminders answers Expired when no retry fits before the
        // next due time, and the next occurrence is already scheduled. The
        // consecutive failure count is the only stop condition for a series.
        var isSeries = definition is { Schedule.Type: not ReminderScheduleType.OneShot };
        var occurrenceTerminal = nack?.ResponseCode is ReminderNackResponseCode.Failed
            || (nack?.ResponseCode is ReminderNackResponseCode.Expired && !isSeries);
        if (nack?.ResponseCode is ReminderNackResponseCode.Error)
        {
            if (definition is not null)
            {
                EmitSettlementFailure(
                    definition,
                    nack.Message ?? $"Negative acknowledgement returned {nack.ResponseCode}.");
            }
        }

        if (endsOneShot && occurrenceTerminal)
        {
            // Akka.Reminders gave up on the occurrence. The nack reply is the first sign of the end, so
            // the tombstone follows the nack (design D3). A crash between them leaves an enabled definition with a
            // past fire time and a terminal durable status, which the startup reconciliation ends.
            var ended = await EndOneShotAsync(
                definition!,
                ReminderTerminalOutcome.Failed,
                reason,
                _timeProvider.GetUtcNow(),
                envelope: null,
                EnvelopeAction.None);
            ReportExecutionFailure(outcome.Id, definition, count, reason, willDisable: ended);
            return;
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

    /// <summary>
    /// Surfaces a scheduling failure — a reminder that could not compute or install
    /// its next occurrence at an unattended reschedule site (the post-fire reschedule
    /// or the startup reconcile). It mirrors the execution-failure path: it bumps the
    /// shared <c>ConsecutiveFailures</c> count, auto-disables at
    /// <see cref="FailurePauseThreshold"/>, and emits an operational alert. The channel
    /// notice is posted only on auto-disable; the Warning alert deduplicates per reminder
    /// in the sink, so a persistent fault does not storm the channel on every restart.
    /// A scheduling failure never falls back to UTC — a wrong-time fire is worse than a
    /// missed one, so an unresolvable zone fails loud instead.
    /// </summary>
    private async Task ReportScheduleFailureAsync(ReminderDefinition definition, string reason)
    {
        var count = definition.ConsecutiveFailures + 1;
        var thresholdReached = count >= FailurePauseThreshold;
        var title = definition.Title;

        // A one-shot that reaches the threshold ends into a tombstone. A recurring reminder is saved
        // disabled and stays until an explicit delete.
        var endsOneShot = thresholdReached && definition.Schedule.Type == ReminderScheduleType.OneShot;
        try
        {
            definition = definition with
            {
                ConsecutiveFailures = count,
                Enabled = thresholdReached && !endsOneShot ? false : definition.Enabled,
                TerminalOutcome = thresholdReached && !endsOneShot ? ReminderTerminalOutcome.Failed : definition.TerminalOutcome,
                UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            };
            _definitionStore.Save(definition);
        }
        catch (Exception ex)
        {
            EmitSettlementFailure(definition, ex.Message, ex);
            return;
        }

        if (endsOneShot)
        {
            if (!await EndOneShotAsync(
                    definition,
                    ReminderTerminalOutcome.Failed,
                    $"The reminder could not be scheduled: {reason}",
                    _timeProvider.GetUtcNow(),
                    envelope: null,
                    EnvelopeAction.None))
            {
                return;
            }
        }
        else if (thresholdReached)
        {
            // On disable, drop any lingering schedule (there usually is none — that is the failure).
            await CancelScheduleOnlyAsync(definition.Id);
        }

        _log.Warning("Reminder '{0}' failed to schedule ({1}/{2}): {3}",
            definition.Id.Value, count, FailurePauseThreshold, reason);

        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "reminder.schedule.failed",
            AlertType.ReminderScheduleFailed,
            $"Reminder '{title}' failed to schedule: {reason}",
            AlertSeverity.Warning,
            source: definition.Id.Value,
            context: new Dictionary<string, string>
            {
                ["reminderId"] = definition.Id.Value,
                ["title"] = title,
                ["error"] = reason
            }));

        if (!thresholdReached)
            return;

        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "reminder.auto_disabled",
            AlertType.ReminderAutoDisabled,
            $"Reminder '{title}' disabled after {count} consecutive scheduling failures",
            AlertSeverity.Critical,
            source: definition.Id.Value,
            context: new Dictionary<string, string>
            {
                ["reminderId"] = definition.Id.Value,
                ["title"] = title,
                ["failureCount"] = count.ToString()
            }));

        PostFailureNoticeToChannel(
            definition,
            $"Reminder \"{title}\" was automatically disabled after {count} consecutive scheduling failures. Last error: {reason}");
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

    /// <summary>What the manager does with the Akka.Reminders envelope at the end of a one-shot.</summary>
    private enum EnvelopeAction
    {
        /// <summary>No envelope is open, or the nack already went out.</summary>
        None,
        Ack,
        Nack
    }

    /// <summary>
    /// Design D4: a tombstone matches a definition when the definition is a one-shot and both fire times are
    /// equal at millisecond precision. A matching tombstone means the one-shot ended, so it wins over its
    /// definition. The id is not compared: the store reads a tombstone by the id of its definition.
    /// </summary>
    internal static bool TombstoneMatches(ReminderTombstone tombstone, ReminderDefinition definition) =>
        definition.Schedule.Type == ReminderScheduleType.OneShot
        && tombstone.FireAtMs == definition.Schedule.FireAtMs;

    /// <summary>
    /// Design D5: the definition is the one that ran when its fire time equals the due time of the settled
    /// occurrence, at millisecond precision (the precision of <see cref="ReminderFireKey"/>).
    /// </summary>
    internal static bool SettledOccurrenceIsCurrent(ReminderDefinition definition, ReminderEnvelope<ReminderPayload> envelope) =>
        definition.Schedule.FireAtMs == envelope.DueTimeUtc.ToUnixTimeMilliseconds();

    /// <summary>
    /// Ends a one-shot with one ordered path (design D3). The tombstone is the commit point: it is the only
    /// step that records the outcome, so it comes first. If Netclaw cannot write it, the end stops here with
    /// an alert, and the one-shot stays enabled. The lease expiry or the next sweep is the recovery path.
    /// Each later step is idempotent, and a matching tombstone finishes it after a crash (design D4).
    /// </summary>
    /// <returns>True when the tombstone is durable.</returns>
    private async Task<bool> EndOneShotAsync(
        ReminderDefinition definition,
        ReminderTerminalOutcome outcome,
        string? lastError,
        DateTimeOffset endedAt,
        ReminderEnvelope<ReminderPayload>? envelope,
        EnvelopeAction action)
    {
        if (definition.Schedule.FireAtMs is not { } fireAtMs)
        {
            EmitSettlementFailure(definition, "The one-shot has no fire time, so Netclaw cannot write its end record.");
            return false;
        }

        var tombstone = new ReminderTombstone
        {
            Id = definition.Id,
            Title = definition.Title,
            Audience = definition.Audience,
            FireAtMs = fireAtMs,
            Outcome = outcome,
            EndedAtMs = endedAt.ToUnixTimeMilliseconds(),
            LastError = lastError,
            // Only a Failed one-shot keeps the prompt, so an operator can set the reminder again (design D1a).
            Instructions = outcome == ReminderTerminalOutcome.Failed ? definition.Instructions : null
        };

        try
        {
            _definitionStore.SaveTombstone(tombstone);
        }
        catch (Exception ex)
        {
            EmitSettlementFailure(definition, $"The end record could not be written: {ex.Message}", ex);
            return false;
        }

        _log.Info("Reminder '{0}' ended as {1}", definition.Id.Value, outcome);
        await FinishEndedOneShotAsync(definition, envelope, action, nackReason: lastError);
        return true;
    }

    /// <summary>
    /// Steps 2 to 5 of the end (design D3), also used when a matching tombstone already exists. A failure of
    /// the envelope action or the delete raises an alert and does not stop the later steps. The tombstone is
    /// durable, so a redelivery or the next sweep finishes whatever stopped.
    /// </summary>
    private async Task FinishEndedOneShotAsync(
        ReminderDefinition definition,
        ReminderEnvelope<ReminderPayload>? envelope,
        EnvelopeAction action,
        string? nackReason)
    {
        if (envelope is not null)
        {
            try
            {
                switch (action)
                {
                    case EnvelopeAction.Ack:
                        var ack = await _client!.AckAsync(envelope);
                        if (ack.ResponseCode is ReminderAckResponseCode.Error)
                            EmitSettlementFailure(definition, ack.Message ?? $"Reminder acknowledgement returned {ack.ResponseCode}.");
                        break;

                    case EnvelopeAction.Nack:
                        var nack = await _client!.NackAsync(envelope, nackReason ?? "The reminder ended.");
                        if (nack.ResponseCode is ReminderNackResponseCode.Error)
                            EmitSettlementFailure(definition, nack.Message ?? $"Negative acknowledgement returned {nack.ResponseCode}.");
                        break;
                }
            }
            catch (Exception ex)
            {
                EmitSettlementFailure(definition, ex.Message, ex);
            }
        }

        await CancelScheduleOnlyAsync(definition.Id);
        _skipCounts.Remove(definition.Id);
        _deniedAlerts.Remove(definition.Id);

        try
        {
            _definitionStore.Delete(definition.Id);
        }
        catch (Exception ex)
        {
            EmitSettlementFailure(definition, $"The definition could not be deleted: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A fire of a one-shot whose tombstone matches it runs nothing. The manager acknowledges the occurrence
    /// and finishes the end. A tombstone that does not match is stale: the definition is newer (design D4).
    /// </summary>
    private async Task<bool> TryFinishMatchedTombstoneAsync(
        ReminderDefinition definition,
        ReminderEnvelope<ReminderPayload> envelope)
    {
        var tombstone = ReadTombstone(definition.Id);
        if (tombstone is null)
            return false;

        if (!TombstoneMatches(tombstone, definition))
        {
            DropStaleTombstone(tombstone, definition);
            return false;
        }

        _log.Info(
            "Reminder '{0}' fired after it ended ({1}). Running nothing, and finishing the end.",
            definition.Id.Value, tombstone.Outcome);
        await FinishEndedOneShotAsync(definition, envelope, EnvelopeAction.Ack, nackReason: null);
        return true;
    }

    private void DropStaleTombstone(ReminderTombstone tombstone, ReminderDefinition definition)
    {
        _log.Warning(
            "Reminder '{0}' has a stale tombstone ({1}, fire time {2}) that does not match its definition ({3}, fire time {4}). Keeping the definition and deleting the tombstone.",
            definition.Id.Value,
            tombstone.Outcome,
            tombstone.FireAtMs,
            definition.Schedule.Type,
            definition.Schedule.FireAtMs?.ToString() ?? "none");
        _definitionStore.DeleteTombstone(definition.Id);
    }

    private ReminderTombstone? ReadTombstone(ReminderId id)
    {
        var tombstone = _definitionStore.GetTombstone(id);
        ReportInvalidTombstones();
        return tombstone;
    }

    /// <summary>
    /// A tombstone that Netclaw cannot read counts as absent, so no read exposes history without an audience
    /// to check. The file stays for the operator. The error log and the alert make the fault loud.
    /// </summary>
    private void ReportInvalidTombstones()
    {
        foreach (var invalid in _definitionStore.ConsumeInvalidTombstones())
        {
            _log.Error(
                "Reminder tombstone for '{0}' cannot be read and counts as absent: {1}",
                invalid.ReminderId, invalid.Reason);
            _notificationSink.Emit(OperationalAlert.Create(
                _timeProvider,
                "reminder.tombstone.invalid",
                AlertType.ReminderSchemaDropped,
                $"The end record of reminder '{invalid.ReminderId}' cannot be read: {invalid.Reason}. Its history is not readable. Repair or delete ended/{Uri.EscapeDataString(invalid.ReminderId)}.json.",
                AlertSeverity.Warning,
                source: invalid.ReminderId,
                context: new Dictionary<string, string>
                {
                    ["reminderId"] = invalid.ReminderId,
                    ["error"] = invalid.Reason
                }));
        }
    }

    private ReminderEndedInfo ToEndedInfo(ReminderTombstone tombstone, bool includeInstructions) => new(
        tombstone.Outcome,
        tombstone.EndedAt,
        tombstone.EndedAt + TerminalRetention,
        tombstone.LastError,
        includeInstructions ? tombstone.Instructions : null);

    /// <summary>
    /// The one sweep of the startup reconciliation and the 12-hour timer (design D8). It finishes the end of
    /// a definition that has a matching tombstone, drops a stale tombstone, ends every leftover one-shot,
    /// and purges each tombstone past <see cref="TerminalRetention"/> with its history. The same rules repair a
    /// crash and migrate an old install.
    /// </summary>
    private async Task<SweepResult> SweepEndedOneShotsAsync()
    {
        var now = _timeProvider.GetUtcNow();
        var tombstones = _definitionStore.ListTombstones().ToDictionary(t => t.Id);
        ReportInvalidTombstones();
        var endedOneShots = 0;

        foreach (var definition in _definitionStore.List())
        {
            if (tombstones.Remove(definition.Id, out var tombstone))
            {
                if (TombstoneMatches(tombstone, definition))
                {
                    await FinishEndedOneShotAsync(definition, envelope: null, EnvelopeAction.None, nackReason: null);
                    endedOneShots++;
                    // The tombstone stays, so the purge below still sees it.
                    tombstones[tombstone.Id] = tombstone;
                    continue;
                }

                DropStaleTombstone(tombstone, definition);
            }

            if (definition.Schedule.Type != ReminderScheduleType.OneShot)
                continue;

            if (definition.TerminalOutcome is { } terminalOutcome)
            {
                if (await EndOneShotAsync(
                        definition,
                        terminalOutcome,
                        "The outcome was recorded before the definition was deleted. Netclaw repaired it.",
                        definition.UpdatedAt,
                        envelope: null,
                        EnvelopeAction.None))
                {
                    endedOneShots++;
                    RememberTombstone(tombstones, definition.Id);
                }

                continue;
            }

            if (!definition.Enabled && definition.Schedule.FireAt is { } fireAt && fireAt <= now)
            {
                // A paused one-shot can never run once its fire time has passed.
                var endedAt = definition.UpdatedAt > fireAt ? definition.UpdatedAt : fireAt;
                if (await EndOneShotAsync(
                        definition,
                        ReminderTerminalOutcome.Cancelled,
                        "The outcome was not recorded. The one-shot was disabled when its fire time passed.",
                        endedAt,
                        envelope: null,
                        EnvelopeAction.None))
                {
                    endedOneShots++;
                    RememberTombstone(tombstones, definition.Id);
                }
            }
        }

        var cutoffMs = now.Subtract(TerminalRetention).ToUnixTimeMilliseconds();
        var purgedTombstones = 0;
        foreach (var tombstone in tombstones.Values.Where(t => t.EndedAtMs < cutoffMs))
        {
            try
            {
                // The history goes first: the tombstone is its only audience anchor.
                _historyStore.DeleteHistory(tombstone.Id);
                _definitionStore.DeleteTombstone(tombstone.Id);
                purgedTombstones++;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to purge the ended one-shot '{0}'", tombstone.Id.Value);
            }
        }

        if (purgedTombstones > 0)
            _log.Info("Purged {0} ended one-shot(s) older than {1}", purgedTombstones, TerminalRetention);

        return new SweepResult(endedOneShots, purgedTombstones);
    }

    private void RememberTombstone(Dictionary<ReminderId, ReminderTombstone> tombstones, ReminderId id)
    {
        if (_definitionStore.GetTombstone(id) is { } written)
            tombstones[id] = written;
    }

    private sealed record SweepResult(int EndedOneShots, int PurgedTombstones);

    private async Task HandleReconcileAsync()
    {
        var sender = Sender; // capture before any await
        try
        {
            // The sweep comes first. It ends leftovers and finishes ends that a crash stopped, so the
            // schedule restore below does not bring a spent one-shot back.
            var sweep = await SweepEndedOneShotsAsync();

            var scheduled = await ListScheduledRemindersAsync();
            var definitions = _definitionStore.List();
            var definitionsById = definitions.ToDictionary(d => d.Id.Value, StringComparer.Ordinal);

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
                {
                    restoredSchedules++;
                }
                else
                {
                    // Do not silently skip a reminder that failed to reschedule. Surface it
                    // and keep going so one bad reminder does not abort reconcile. A single
                    // bad startup only bumps each count by one, so it cannot mass-disable.
                    await ReportScheduleFailureAsync(
                        definition,
                        result.ErrorMessage ?? "Failed to restore reminder schedule at startup.");
                }
            }

            // Issue #1803: a past due time and the absence of a schedule do not prove success.
            var now = _timeProvider.GetUtcNow();
            var endedFromStatus = 0;
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

                var lastError = outcome == ReminderTerminalOutcome.Completed
                    ? null
                    : occurrence.LastFailureReason
                      ?? $"Akka.Reminders ended the occurrence with the status {occurrence.CompletionStatus}.";
                if (await EndOneShotAsync(
                        definition,
                        outcome.Value,
                        lastError,
                        occurrence.CompletedAtUtc ?? now,
                        envelope: null,
                        EnvelopeAction.None))
                {
                    endedFromStatus++;
                }
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

            var endedOneShots = sweep.EndedOneShots + endedFromStatus;
            if (cancelledOrphans > 0 || restoredSchedules > 0 || endedOneShots > 0
                || disabledExpired > 0 || sweep.PurgedTombstones > 0)
            {
                _log.Info("Reminder reconcile complete: cancelled_orphans={0}, restored={1}, ended_oneshots={2}, disabled_expired={3}, purged_tombstones={4}",
                    cancelledOrphans,
                    restoredSchedules,
                    endedOneShots,
                    disabledExpired,
                    sweep.PurgedTombstones);
            }

            // Only ack external callers — skip Self.Tell from PreStart
            if (!sender.Equals(Self))
                sender.Tell(new ReconcileCompleted(cancelledOrphans, restoredSchedules, endedFromStatus, disabledExpired));
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

        var actorName = $"exec-{SanitizeActorName(definition.Id.Value)}-{++_executionSequence}";
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
            _definitionStore.List().Count(d => d.Enabled && d.ConsecutiveFailures > 0)));
    }

    private async Task HandleGetStatusAsync(GetReminderStatusQuery query)
    {
        var replyTo = Sender;
        try
        {
            var definition = _definitionStore.Get(query.Id);
            if (definition is null)
            {
                var ended = ReadTombstone(query.Id);
                if (ended is not null && CanAccessAudience(query.Authorization, ended.Audience))
                {
                    replyTo.Tell(await BuildEndedStatusAsync(ended));
                    return;
                }

                if (ended is not null)
                    LogAudienceDenied("status", query.Id, ended.Audience, query.Authorization);
            }

            if (definition is null || !CanAccessAudience(query.Authorization, definition.Audience))
            {
                if (definition is not null)
                    LogAudienceDenied("status", query.Id, definition.Audience, query.Authorization);

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

            var occurrence = ToOccurrenceInfo(occurrenceTask.Result);

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

    /// <summary>
    /// The status of an ended one-shot, from its tombstone. This is the one surface that carries the prompt of a
    /// <c>Failed</c> tombstone, and the caller passed the audience gate before it got here.
    /// </summary>
    private async Task<ReminderStatusResponse> BuildEndedStatusAsync(ReminderTombstone ended)
    {
        var historyTask = _historyStore.ReadAsync(ended.Id, RecentHistoryCount);
        var occurrenceTask = GetOccurrenceStatusAsync(ended.Id, DateTimeOffset.FromUnixTimeMilliseconds(ended.FireAtMs));
        await Task.WhenAll(historyTask, occurrenceTask);

        return new ReminderStatusResponse(
            ended.Id,
            Found: true,
            Enabled: false,
            Executing: _activeExecutions.IsExecuting(ended.Id),
            NextFire: null,
            ConsecutiveFailures: 0,
            SkippedDuplicates: _skipCounts.GetValueOrDefault(ended.Id),
            TerminalOutcome: ended.Outcome,
            Occurrence: ToOccurrenceInfo(occurrenceTask.Result),
            RecentHistory: historyTask.Result,
            Ended: ToEndedInfo(ended, includeInstructions: true));
    }

    private static ReminderOccurrenceInfo? ToOccurrenceInfo(ReminderOccurrenceStatus? status) => status is null
        ? null
        : new ReminderOccurrenceInfo(
            status.DueTimeUtc,
            status.NextAttemptAtUtc,
            status.AttemptCount,
            status.LastFailureReason,
            status.CompletionStatus.ToString(),
            status.DeliveryDeadlineUtc,
            status.AckDeadlineUtc,
            status.CompletedAtUtc);

    private async Task HandleGetHistoryAsync(GetReminderHistoryQuery query)
    {
        var replyTo = Sender;
        try
        {
            // An ended one-shot has no definition. Its tombstone supplies the audience for the history.
            var definition = _definitionStore.Get(query.Id);
            var audience = definition?.Audience ?? ReadTombstone(query.Id)?.Audience;
            if (audience is not { } historyAudience || !CanAccessAudience(query.Authorization, historyAudience))
            {
                if (audience is { } deniedAudience)
                    LogAudienceDenied("history", query.Id, deniedAudience, query.Authorization);

                replyTo.Tell(new ReminderHistoryResponse(query.Id, Found: false, Records: []));
                return;
            }

            var records = await _historyStore.ReadAsync(query.Id, query.MaxRecords);
            replyTo.Tell(new ReminderHistoryResponse(query.Id, Found: true, Records: records));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error getting history for reminder '{0}'", query.Id.Value);
            replyTo.Tell(new ReminderHistoryResponse(query.Id, Found: false, Records: []));
        }
    }

    /// <summary>
    /// The one place that decides whether a caller may see or act on a reminder.
    /// A caller may access a reminder only when the reminder's audience is at or
    /// below the caller's audience: Personal &gt; Team &gt; Public — a Personal
    /// caller can reach Personal, Team, and Public reminders; a Team caller can
    /// reach Team and Public; a Public caller can reach only Public.
    /// <para>
    /// A missing <paramref name="authorization"/>, or one with no
    /// <see cref="ReminderAudienceAuthorizationContext.SourceAudience"/>, resolves
    /// no caller audience at all and therefore denies access — it never grants it.
    /// This intentionally does not use <c>authorization?.SourceAudience</c>: that
    /// shorthand is how a missing value would silently widen access elsewhere, and
    /// here it must do the opposite.
    /// </para>
    /// <para>
    /// <see cref="ReminderDefinition.Audience"/> is a required, non-nullable field,
    /// and <see cref="ReminderDefinitionStore"/> rejects and never loads a document
    /// that predates it (see <c>LegacyTrustFieldGuard</c>), so a definition reaching
    /// this method always carries a real audience. There is no "unknown audience"
    /// branch to fall back on here; if that guarantee is ever relaxed, an unknown
    /// audience must resolve to <see cref="TrustAudience.Personal"/> — the most
    /// restrictive value — never to <see cref="TrustAudience.Team"/> or
    /// <see cref="TrustAudience.Public"/>.
    /// </para>
    /// </summary>
    internal static bool CanAccessAudience(
        ReminderAudienceAuthorizationContext? authorization,
        TrustAudience reminderAudience)
    {
        if (authorization is null || authorization.SourceAudience is not { } callerAudience)
            return false;

        return reminderAudience <= callerAudience;
    }

    /// <summary>
    /// Logs an audience-scoped denial at Info so an operator can see access-control
    /// activity without a debug build, while keeping the record free of reminder
    /// content — only the id, its audience, and the caller's audience.
    /// </summary>
    private void LogAudienceDenied(
        string operation,
        ReminderId id,
        TrustAudience reminderAudience,
        ReminderAudienceAuthorizationContext? authorization)
    {
        _log.Info(
            "Reminder '{0}' (audience={1}) is out of scope for a {2} caller (audience={3}, source={4}); treating as not found.",
            id.Value,
            reminderAudience.ToWireValue(),
            operation,
            authorization?.SourceAudience?.ToWireValue() ?? "none",
            authorization?.SourceDescription ?? "unknown");
    }

    private Task<ReminderOccurrenceStatus?> GetOccurrenceStatusAsync(ReminderDefinition definition) =>
        definition.Schedule.Type is not ReminderScheduleType.OneShot || definition.Schedule.FireAt is not { } dueTimeUtc
            ? Task.FromResult<ReminderOccurrenceStatus?>(null)
            : GetOccurrenceStatusAsync(definition.Id, dueTimeUtc);

    private async Task<ReminderOccurrenceStatus?> GetOccurrenceStatusAsync(ReminderId id, DateTimeOffset dueTimeUtc)
    {
        if (_client is null)
            return null;

        var response = await _client.GetOccurrenceStatusAsync(
            new ReminderKey(id.Value),
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

    internal sealed record PruneTerminalReminders : INoSerializationVerificationNeeded
    {
        public static readonly PruneTerminalReminders Instance = new();
    }

    /// <summary>
    /// Ack sent back to <see cref="ReconcileReminders"/> callers so they can
    /// synchronize on reconcile completion instead of polling.
    /// <see cref="SoftDeletedOneShots"/> counts the one-shots that the durable occurrence status ended.
    /// </summary>
    internal sealed record ReconcileCompleted(
        int CancelledOrphans,
        int RestoredSchedules,
        int SoftDeletedOneShots,
        int DisabledExpired = 0) : INoSerializationVerificationNeeded;
}
