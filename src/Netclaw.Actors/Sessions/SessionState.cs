// -----------------------------------------------------------------------
// <copyright file="SessionState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.SubAgents;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Immutable conversation state for an LLM session. Decoupled from the actor
/// so that state transitions (event application) are pure functions testable
/// without an ActorSystem.
///
/// The actor holds a single <c>SessionState</c> field and replaces it on each
/// event via the <c>Apply</c> methods. Transient concerns (subscribers, message
/// buffer, behavior) remain on the actor.
/// </summary>
public sealed partial record SessionState
{
    public sealed record AdoptedContextAuditRecord(
        string AuthorizedMessageId,
        SenderId? AuthorizerSenderId,
        string? LowerBound,
        string? UpperBound,
        string Projection,
        bool HasAdoptedContext,
        bool HasThirdPartyAdoptedContext,
        ImmutableList<string> AdoptedSpeakerIds,
        bool ProjectionPersisted,
        ImmutableList<AdoptedContextAuditMessage> Messages);

    public sealed record AdoptedContextAuditMessage(
        string MessageId,
        SenderId SenderId,
        DateTimeOffset Timestamp,
        string AuthorityAtInclusion);

    public static readonly SessionState Empty = new();
    internal const string SystemNudgePrefix = "[system:";

    public ImmutableList<SerializableChatMessage> History { get; init; } =
        [];

    public ImmutableList<InputAdmitted> PendingInputs { get; init; } = [];

    public ImmutableList<string> RecentSourceMessageKeys { get; init; } = [];

    public ToolLoopCheckpoint LoopCheckpoint { get; init; } = new();
    public ToolLoopAdmission? LoopAdmission { get; init; }
    public ImmutableList<ToolLoopObservation> LoopObservations { get; init; } = [];
    public bool LoopReceiptFailure { get; init; }
    public TurnContextRecord? AdoptedTaskContext { get; init; }
    public IReadOnlyList<InputId> AdoptedTaskInputIds { get; init; } = [];

    public ImmutableDictionary<Netclaw.Tools.SubAgentRunId, BackgroundChildRun> ChildRuns { get; init; } = [];

    public int TurnCount { get; init; }

    public string? Title { get; init; }

    /// <summary>
    /// Durable state for "what the agent is currently working on" — recent
    /// files, open goals, and progress markers. Survives compaction, actor
    /// recovery, and daemon restart. Injected as a <c>[working-context]</c>
    /// block on every LLM call when non-empty. Updated primarily by
    /// tool-execution hooks; observer output may opportunistically enrich
    /// it after compaction.
    /// </summary>
    public WorkingContext WorkingContext { get; init; } = WorkingContext.Empty;

    /// <summary>
    /// In-memory best-effort dedup ledger for reminder-originated turns.
    /// Populated by <see cref="Apply(TurnRecorded)"/> from non-null
    /// <see cref="TurnRecorded.SourceReminderId"/> values and preserved
    /// across compaction by <see cref="Apply(SessionCompacted)"/>.
    /// Deliberately NOT persisted to <see cref="SessionSnapshot"/> — on
    /// snapshot-based recovery the set starts empty and rebuilds from
    /// post-snapshot journal replay. Duplicates across snapshot recovery
    /// boundaries are an explicitly accepted tradeoff; see
    /// <c>reminder-session-reentry</c> design doc D2.
    /// </summary>
    public IImmutableSet<ReminderId> ProcessedReminderIds { get; init; } =
        ImmutableHashSet<ReminderId>.Empty;

    /// <summary>
    /// Background jobs this session is waiting on. Persisted to snapshot
    /// because jobs are long-lived and must survive recovery.
    /// </summary>
    public ImmutableDictionary<string, ActiveJobInfo> ActiveBackgroundJobs { get; init; } =
        [];

    public ImmutableDictionary<string, AdoptedContextAuditRecord> AdoptedContextRecords { get; init; } =
        [];

    /// <summary>
    /// Durable deduplication for completed and rejected background-job deliveries.
    /// Pre-change snapshots omit this evidence and can have a legacy gap.
    /// </summary>
    public IImmutableSet<BackgroundJobId> ProcessedBackgroundJobIds { get; init; } =
        ImmutableHashSet<BackgroundJobId>.Empty;

    // ── Event application (pure functions) ──

    public SessionState Apply(ChildRunAccepted evt)
    {
        var run = evt.Run;
        run.Validate();
        if (run.State != BackgroundChildState.Accepted || run.ChildCheckpoint is not null || run.DeliveryInputId is not null)
            throw new InvalidDataException("A child acceptance contains facts from a later lifecycle stage.");
        if (evt.SessionId != run.StartKey.SessionId || AdoptedTaskContext is null
            || !SameCanonicalContext(AdoptedTaskContext, run.OriginalContext)
            || !AdoptedTaskInputIds.SequenceEqual(run.OriginInputIds)
            || !BackgroundChildRun.SameCheckpoint(run.ParentCheckpoint, LoopCheckpoint)
            || run.ParentReceiptFailure != LoopReceiptFailure)
            throw new InvalidDataException("A child acceptance differs from the canonical parent task.");
        if (ChildRuns.ContainsKey(run.RunId) || ChildRuns.Values.Any(existing => existing.StartKey == run.StartKey))
            throw new InvalidDataException("A child acceptance repeats an already committed start identity.");
        if (run.StartKey is ChildRunStartKey.Tool tool
            && (run.StartBatchSettled || LoopAdmission is null
                || !LoopAdmission.Calls.Any(call => call.CallId == tool.CallId.Value && call.ToolName == run.SourceOperation)))
            throw new InvalidDataException("A child acceptance does not belong to its admitted parent tool call.");
        return this with { ChildRuns = ChildRuns.Add(run.RunId, run) };
    }

    public SessionState Apply(ToolTaskAdopted evt)
    {
        if (!TurnContext.TryFromRecord(evt.TurnContext, out var context, out var reason) || context is null)
            throw new InvalidDataException($"An adopted task has invalid authority: {reason}");
        if (evt.SessionId != context.SessionId)
            throw new InvalidDataException("An adopted task has a different session identity.");
        if (evt.StartsChildContinuationWindow && evt.ContinuedChildRunId is null)
            throw new InvalidDataException("Only a canonical child continuation can start a new recurrence window.");
        if (evt.ContinuedChildRunId is not null)
            return AdoptChildContinuation(evt);
        if (PendingInputs.Take(evt.InputIds.Count).Any(static input => input.SourceChildRunId is not null))
            throw new InvalidDataException("A child delivery requires its explicit continuation identity.");
        if (evt.ContinuedJobKey is not null && (evt.ContinuedJobKey != context.TurnId.Value
            || evt.TurnContext.SourceKind != BackgroundJobManagerActor.SourceKind))
            throw new InvalidDataException("A continued job key differs from its canonical authority context.");
        if (AdoptedTaskContext is { } currentRecord && currentRecord.TurnId == context.TurnId.Value)
        {
            if (!TurnContext.TryFromRecord(currentRecord, out var current, out reason) || current is null
                || !SameCanonicalContext(evt.TurnContext, currentRecord) || !AdoptedTaskInputIds.SequenceEqual(evt.InputIds)
                || (currentRecord.SourceKind == BackgroundJobManagerActor.SourceKind && evt.ContinuedJobKey != currentRecord.TurnId))
                throw new InvalidDataException("A repeated adoption differs from its canonical task.");
            return this;
        }
        if (evt.InputIds.Count == 0 || evt.InputIds.Distinct().Count() != evt.InputIds.Count
            || !PendingInputs.Take(evt.InputIds.Count).Select(static input => input.InputId).SequenceEqual(evt.InputIds))
            throw new InvalidDataException("An adopted task does not name the next input prefix.");
        var prefix = PendingInputs.Take(evt.InputIds.Count).ToArray();
        foreach (var input in prefix)
        {
            if (!TurnContext.TryFromRecord(input.TurnContext, out var canonical, out reason) || canonical is null
                || !TurnContext.HasSameAuthority(canonical, context))
                throw new InvalidDataException("An adopted task differs from admitted input authority.");
        }
        if (!SameCanonicalContext(evt.TurnContext, prefix[^1].TurnContext))
            throw new InvalidDataException("An adopted task does not use the latest admitted prefix context.");
        var checkpoint = new ToolLoopCheckpoint { TaskId = context.TurnId.Value };
        var receiptFailure = false;
        if (prefix[^1].SourceBackgroundJobId is { } jobId)
        {
            var validLineage = TryGetBackgroundContinuation(prefix[^1], out var job, out var lineageReason);
            if (prefix.Length != 1 || evt.ContinuedJobKey != jobId.Value || !validLineage)
                throw new InvalidDataException($"A task cannot continue its job lineage: {lineageReason}");
            if (job is not null)
            {
                checkpoint = job.OriginCheckpoint!;
                receiptFailure = job.OriginReceiptFailure;
            }
        }
        else if (evt.ContinuedJobKey is not null)
            throw new InvalidDataException("A non-job input cannot restore a job checkpoint.");
        return this with
        {
            AdoptedTaskContext = evt.TurnContext, AdoptedTaskInputIds = evt.InputIds,
            LoopCheckpoint = checkpoint,
            LoopAdmission = null, LoopObservations = [], LoopReceiptFailure = receiptFailure
        };
    }

    public SessionState ApplyLoopAdmission(ToolBatchStarted evt)
    {
        if (!evt.MetadataOnly && evt.LegacyTaskContext is not null)
            throw new InvalidDataException("A legacy task context requires a metadata-only event.");
        if (evt.MetadataOnly)
        {
            if (!TurnContext.TryFromRecord(evt.LegacyTaskContext, out var legacyContext, out var contextReason) || legacyContext is null
                || legacyContext.SessionId != evt.SessionId || legacyContext.TurnId.Value != evt.LoopAdmission?.TaskId)
                throw new InvalidDataException($"A legacy baseline has invalid canonical authority: {contextReason}");
            if (evt.LoopAdmission is not { Calls.Count: > 0 } admission || evt.LoopDelta is not { Reset: true } delta
                || admission.TaskId != delta.TaskId || admission.RefusedCallIds.Count != 0
                || evt.ConsumedInputIds.Count != 0 || HasMessagePayload(evt.UserMessage) || HasMessagePayload(evt.AssistantMessage)
                || delta.Upserts.Count != 0 || delta.RemovedKeys.Count != 0 || delta.ColdKeys.Count != 0
                || delta.AdjacentHistory.Count != 0 || delta.LastBlockedAction is not null)
                throw new InvalidDataException("A legacy tool baseline has invalid metadata-only representation.");
            if (LoopAdmission is { } existing)
            {
                if (!SameAdmission(existing, admission) || AdoptedTaskContext is null
                    || !SameCanonicalContext(AdoptedTaskContext, evt.LegacyTaskContext!))
                    throw new InvalidDataException("A repeated legacy tool baseline differs from its committed admission.");
                return this;
            }
            if (!string.IsNullOrEmpty(LoopCheckpoint.TaskId) || LoopCheckpoint.Entries.Count != 0
                || LoopCheckpoint.ColdKeys.Count != 0 || LoopCheckpoint.AdjacentHistory.Count != 0
                || LoopCheckpoint.LastBlockedAction is not null || LoopObservations.Count != 0 || LoopReceiptFailure)
                throw new InvalidDataException("A legacy tool baseline cannot replace new-format evidence.");
        }
        return evt.LoopAdmission is null || evt.LoopDelta is null ? this : (this with
        {
            LoopCheckpoint = TurnStateTracker.ApplyDelta(LoopCheckpoint, evt.LoopDelta),
            LoopAdmission = evt.LoopAdmission, LoopObservations = [], LoopReceiptFailure = false,
            AdoptedTaskContext = evt.MetadataOnly ? evt.LegacyTaskContext : AdoptedTaskContext,
            AdoptedTaskInputIds = evt.MetadataOnly ? [] : AdoptedTaskInputIds
        }).RefreshJobEvidence();
    }

    private static bool HasMessagePayload(SerializableChatMessage message)
        => !string.IsNullOrEmpty(message.Content) || message.ToolCalls.Count != 0 || message.MediaReferences.Count != 0
           || message.ToolCallId is not null || message.Name is not null || message.Role != ChatRole.User;

    internal static bool SameAdmission(ToolLoopAdmission left, ToolLoopAdmission right)
        => left.TaskId == right.TaskId && left.ActionHash == right.ActionHash
           && left.Calls.SequenceEqual(right.Calls) && left.RefusedCallIds.SequenceEqual(right.RefusedCallIds);

    public SessionState ApplyLoopObservation(ToolCallRecorded evt)
    {
        if (LoopAdmission is not { } admission || evt.LoopObservation is not { } observation
            || !admission.Calls.Any(call => call.CallId == observation.CallId)
            || LoopObservations.Any(item => item.CallId == observation.CallId))
            return this;
        var owner = this;
        if (evt.StartedBackgroundJob is { } started)
        {
            if (started.LineageVersion != 1 || started.Origin is null
                || started.Origin.TurnId.Value != admission.TaskId || started.Origin.CallId.Value != observation.CallId)
                throw new InvalidDataException("A started job differs from canonical parent admission.");
            started.Origin.Validate();
            owner = TrackBackgroundJob($"{BackgroundJobManagerActor.JobDeliveryKeyPrefix}{started.JobId.Value}",
                started with { OriginCheckpoint = LoopCheckpoint, OriginReceiptFailure = LoopReceiptFailure });
        }
        var observations = LoopObservations.Add(observation);
        var next = owner with { LoopObservations = observations,
            LoopReceiptFailure = LoopReceiptFailure || observation.MissingReceipt };
        if (observations.Count != admission.Calls.Count || next.LoopReceiptFailure)
            return next.RefreshJobEvidence();
        var tracker = new TurnStateTracker();
        tracker.RestoreCheckpoint(LoopCheckpoint);
        if (admission.Calls.Count != admission.RefusedCallIds.Count)
            tracker.ObserveCompleted(ToolCycleSignatureFactory.CompleteEvidence(admission, observations));
        return (next with { LoopCheckpoint = tracker.CaptureCheckpoint(admission.TaskId) }).RefreshJobEvidence();
    }

    internal bool TryGetBackgroundContinuation(InputAdmitted input, out ActiveJobInfo? job, out string? reason)
    {
        job = null;
        reason = "invalid trusted job metadata";
        if (input.SessionId != input.TurnContext.SessionId
            || input.SourceBackgroundJobId is not { } sourceJob || input.SourceMessageId != sourceJob.Value
            || input.TurnContext.TurnId != sourceJob.Value
            || input.TurnContext.SourceKind != BackgroundJobManagerActor.SourceKind
            || input.TurnContext.RequesterPrincipal != Configuration.PrincipalClassification.VerifiedAutomation
            || input.TurnContext.TransportAuthenticity != Configuration.TransportAuthenticity.LocalProcess
            || !sourceJob.Value.StartsWith(BackgroundJobManagerActor.JobDeliveryKeyPrefix, StringComparison.Ordinal))
            return false;
        if (input.BackgroundJobLineageVersion == 0 && input.BackgroundJobOrigin is null)
        {
            if (ActiveBackgroundJobs.TryGetValue(sourceJob.Value, out var tracked) && tracked.LineageVersion != 0)
            {
                reason = "a legacy claim conflicts with committed new-format lineage";
                return false;
            }
            reason = null;
            return true;
        }
        if (input.BackgroundJobLineageVersion != 1 || input.BackgroundJobOrigin is null)
            return false;
        if (!ActiveBackgroundJobs.TryGetValue(sourceJob.Value, out var owned)
            || owned.LineageVersion != 1 || owned.Origin is not { } origin || origin != input.BackgroundJobOrigin
            || string.IsNullOrWhiteSpace(origin.TurnId.Value) || string.IsNullOrWhiteSpace(origin.CallId.Value)
            || sourceJob.Value != $"{BackgroundJobManagerActor.JobDeliveryKeyPrefix}{owned.JobId.Value}"
            || owned.Audience != input.TurnContext.Audience || owned.Boundary != input.TurnContext.Boundary
            || owned.OriginCheckpoint?.TaskId != origin.TurnId.Value)
        {
            reason = "missing or mismatched committed parent lineage";
            return false;
        }
        job = owned;
        reason = null;
        return true;
    }

    private SessionState RefreshJobEvidence()
    {
        var jobs = ActiveBackgroundJobs;
        foreach (var (key, job) in jobs)
        {
            if (job.LineageVersion == 1 && job.Origin?.TurnId.Value == LoopCheckpoint.TaskId)
                jobs = jobs.SetItem(key, job with { OriginCheckpoint = LoopCheckpoint, OriginReceiptFailure = LoopReceiptFailure });
        }
        var runs = ChildRuns;
        foreach (var (id, run) in runs)
        {
            if (run.ParentCheckpoint.TaskId != LoopCheckpoint.TaskId)
                continue;
            var settled = run.StartBatchSettled
                          || run.StartKey is ChildRunStartKey.Tool tool
                          && LoopAdmission is { } admission
                          && admission.Calls.Any(call => call.CallId == tool.CallId.Value)
                          && admission.Calls.All(call => LoopObservations.Any(observation => observation.CallId == call.CallId));
            runs = runs.SetItem(id, run with
            {
                ParentCheckpoint = LoopCheckpoint,
                ParentReceiptFailure = LoopReceiptFailure,
                StartBatchSettled = settled
            });
        }
        return this with { ActiveBackgroundJobs = jobs, ChildRuns = runs };
    }

    internal static bool SameCanonicalContext(TurnContextRecord left, TurnContextRecord right)
        => left with { AdoptedSpeakerIds = right.AdoptedSpeakerIds } == right
           && left.AdoptedSpeakerIds.SequenceEqual(right.AdoptedSpeakerIds, StringComparer.Ordinal);

    public SessionState Apply(InputClosed evt)
    {
        var next = CloseInputs(evt.InputIds);
        if (evt.SourceBackgroundJobId is { } closedJob)
        {
            if (ProcessedBackgroundJobIds.Contains(closedJob) && evt.InputIds.All(id => !PendingInputs.Any(input => input.InputId == id)))
                return this;
            var input = PendingInputs.SingleOrDefault(item => item.InputId == evt.InputIds.SingleOrDefault());
            if (input is null || input.SourceBackgroundJobId != closedJob || input.TurnContext.TurnId != evt.TaskId)
                throw new InvalidDataException("A closed job delivery differs from canonical admitted input.");
            if (input.TurnContext.SourceKind != BackgroundJobManagerActor.SourceKind
                || input.TurnContext.RequesterPrincipal != Configuration.PrincipalClassification.VerifiedAutomation
                || input.TurnContext.TransportAuthenticity != Configuration.TransportAuthenticity.LocalProcess)
                throw new InvalidDataException("A rejected delivery lacks canonical runtime provenance.");
            if (evt.RejectedJobReport is { } report)
            {
                if (report.Role != ChatRole.Assistant || report.ToolCalls.Count > 0 || report.ToolCallId is not null)
                    throw new InvalidDataException("A rejected job report cannot carry tool authority.");
                next = next with { History = next.History.Add(report) };
            }
            next = next with { ProcessedBackgroundJobIds = next.ProcessedBackgroundJobIds.Add(closedJob) };
        }
        if (evt.RejectedJobReport is not null && evt.SourceBackgroundJobId is null)
            throw new InvalidDataException("A rejected job report requires its canonical delivery identity.");
        var closesTask = AdoptedTaskContext is not null
            && (evt.TaskId == AdoptedTaskContext.TurnId
                || (evt.TaskId is null && AdoptedTaskInputIds.Count > 0
                    && AdoptedTaskInputIds.All(evt.InputIds.Contains)));
        return closesTask ? next with { AdoptedTaskContext = null, AdoptedTaskInputIds = [] } : next;
    }

    public SessionState Apply(InputAdmitted evt)
    {
        if (evt.SourceChildRunId is not null)
            throw new InvalidDataException("A child input requires atomic delivery admission.");
        return AdmitInputCore(evt);
    }

    private SessionState AdmitInputCore(InputAdmitted evt)
    {
        var sourceKey = SourceMessageKey(evt);
        var keys = sourceKey is null || RecentSourceMessageKeys.Contains(sourceKey)
            ? RecentSourceMessageKeys
            : RecentSourceMessageKeys.Add(sourceKey);
        if (keys.Count > 256)
            keys = keys.RemoveRange(0, keys.Count - 256);

        return this with
        {
            PendingInputs = PendingInputs.Add(evt),
            RecentSourceMessageKeys = keys
        };
    }

    public SessionState CloseInputs(IReadOnlyList<InputId> inputIds)
    {
        if (inputIds.Count == 0)
            return this;

        var ids = inputIds.ToHashSet();
        return this with { PendingInputs = PendingInputs.RemoveAll(evt => ids.Contains(evt.InputId)) };
    }

    public static string? SourceMessageKey(InputAdmitted evt)
        => string.IsNullOrWhiteSpace(evt.SourceMessageId)
            ? null
            : $"{evt.TurnContext.ChannelType ?? string.Empty}:{evt.TurnContext.RequesterSenderId?.Value ?? string.Empty}:{evt.SourceMessageId}";

    public SessionState Apply(TurnRecorded evt)
    {
        var processedReminders = ProcessedReminderIds;
        if (evt.SourceReminderId is { } reminderId && !string.IsNullOrEmpty(reminderId.Value))
            processedReminders = processedReminders.Add(reminderId);

        // Background-job dedup/remove/prune is delegated to the single shared
        // helper so the replay path here and the live turn-completion path in
        // LlmSessionActor cannot drift.
        return (this with
        {
            History = History.Add(evt.UserMessage).Add(evt.AssistantReply),
            TurnCount = TurnCount + 1,
            ProcessedReminderIds = processedReminders
        }).CompleteTurnBackgroundJobBookkeeping(evt.SourceBackgroundJobId);
    }

    /// <summary>
    /// Single source of truth for per-turn background-job bookkeeping, shared by
    /// the replay path (<see cref="Apply(TurnRecorded)"/>) and the live
    /// turn-completion path (LlmSessionActor): dedup-records a delivery turn's
    /// job ID, removes the delivered entry, and prunes reaped entries that were
    /// surfaced in this turn's context block (so the agent learns of a reap
    /// exactly once instead of on every turn forever).
    /// </summary>
    public SessionState CompleteTurnBackgroundJobBookkeeping(BackgroundJobId? sourceBackgroundJobId)
    {
        var processedJobs = ProcessedBackgroundJobIds;
        var activeJobs = ActiveBackgroundJobs;
        if (sourceBackgroundJobId is { } jobId && !string.IsNullOrEmpty(jobId.Value))
        {
            processedJobs = processedJobs.Add(jobId);
            activeJobs = activeJobs.Remove(jobId.Value);
        }

        activeJobs = PruneReaped(activeJobs);

        return this with
        {
            AdoptedTaskContext = null, AdoptedTaskInputIds = [],
            ProcessedBackgroundJobIds = processedJobs,
            ActiveBackgroundJobs = activeJobs
        };
    }

    public SessionState Apply(SessionTitleSet evt)
    {
        return this with { Title = evt.Title };
    }

    public SessionState Apply(SessionBackgroundJobsReaped evt)
        => MarkAllBackgroundJobsReaped(evt.ReapedAtMs);

    public SessionState Apply(AdoptedContextRecorded evt)
    {
        var record = new AdoptedContextAuditRecord(
            evt.AuthorizedMessageId,
            evt.AuthorizerSenderId,
            evt.LowerBound,
            evt.UpperBound,
            evt.Projection,
            evt.HasAdoptedContext,
            evt.HasThirdPartyAdoptedContext,
            [.. evt.AdoptedSpeakerIds],
            evt.ProjectionPersisted,
            [.. evt.Messages
                .Select(message => new AdoptedContextAuditMessage(
                    message.MessageId,
                    message.SenderId,
                    DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampMs),
                    message.AuthorityAtInclusion))]);

        return this with
        {
            AdoptedContextRecords = AdoptedContextRecords.SetItem(evt.AuthorizedMessageId, record)
        };
    }

    public SessionState Apply(SessionCompacted evt)
    {
        // Preserve system prompt if present, then layer the compacted messages.
        // Summaries are recognizable by their [session-summary session:{id}]
        // header — no separate index is persisted. The reducer's
        // user-message-boundary walk-back naturally preserves prior summary
        // messages because they use User-role and are distinctive.
        var builder = ImmutableList.CreateBuilder<SerializableChatMessage>();
        if (History.Count > 0 && History[0].Role == ChatRole.System)
        {
            builder.Add(History[0]);
        }

        builder.AddRange(evt.CompactedMessages);

        return this with
        {
            History = builder.ToImmutable(),
            WorkingContext = evt.WorkingContext ?? WorkingContext,
            ProcessedReminderIds = ProcessedReminderIds,
            ProcessedBackgroundJobIds = ProcessedBackgroundJobIds,
            ActiveBackgroundJobs = ActiveBackgroundJobs,
            AdoptedContextRecords = AdoptedContextRecords
        };
    }

    public SessionState TrackBackgroundJob(string jobKey, ActiveJobInfo info)
    {
        return this with
        {
            ActiveBackgroundJobs = ActiveBackgroundJobs.SetItem(jobKey, info)
        };
    }

    /// <summary>
    /// Marks every tracked job as reaped (killed at session passivation). The
    /// marked state is captured by the passivation snapshot so the next
    /// rehydration surfaces the reap to the agent exactly once.
    /// </summary>
    public SessionState MarkAllBackgroundJobsReaped(long reapedAtMs)
    {
        if (ActiveBackgroundJobs.IsEmpty)
            return this;

        var marked = ActiveBackgroundJobs;
        foreach (var (key, job) in marked)
        {
            if (job.ReapedAtMs is null)
                marked = marked.SetItem(key, job with { ReapedAtMs = reapedAtMs });
        }

        return this with { ActiveBackgroundJobs = marked };
    }

    private static ImmutableDictionary<string, ActiveJobInfo> PruneReaped(
        ImmutableDictionary<string, ActiveJobInfo> activeJobs)
    {
        foreach (var (key, job) in activeJobs)
        {
            if (job.ReapedAtMs is not null)
                activeJobs = activeJobs.Remove(key);
        }

        return activeJobs;
    }

    // ── Command helpers ──

    /// <summary>
    /// Add a user message to history (before firing an LLM call).
    /// This is transient state that gets persisted as part of <see cref="TurnRecorded"/>.
    /// </summary>
    public SessionState AddUserMessage(string content, IReadOnlyList<SerializableMediaReference>? mediaReferences = null)
    {
        // Snapshot the caller's list: SerializableChatMessage is immutable and must
        // own its media references, so a caller that reuses/clears its list after
        // this call cannot retroactively empty the persisted message (see
        // BuildNudgeMessage for the concrete hazard this guards against).
        var msg = mediaReferences is { Count: > 0 }
            ? new SerializableChatMessage { Role = ChatRole.User, Content = content, MediaReferences = [.. mediaReferences] }
            : new SerializableChatMessage { Role = ChatRole.User, Content = content };

        return this with { History = History.Add(msg) };
    }

    /// <summary>
    /// Add an error reply to history when an LLM call fails.
    /// </summary>
    public SessionState AddErrorReply(string errorMessage)
    {
        return this with
        {
            History = History.Add(new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                Content = errorMessage
            }),
            TurnCount = TurnCount + 1
        };
    }

    /// <summary>
    /// Add a transient system nudge to the END of history to correct LLM
    /// behavior mid-turn (empty-response retry, exact tool recurrence,
    /// delivery retry). These are course-correcting instructions: the model is
    /// meant to act on them, so they sit at the tail where the chat template
    /// treats them as the most recent input. Not persisted as a turn — just
    /// injected into the conversation to guide the next LLM call.
    /// </summary>
    public SessionState AddSystemNudge(
        string nudge,
        IReadOnlyList<SerializableMediaReference>? mediaReferences = null)
    {
        var message = BuildNudgeMessage(nudge, mediaReferences);
        return this with { History = History.Add(message) };
    }

    /// <summary>
    /// Insert the per-turn volatile context block (memory recall, current time,
    /// working context, skill hint, slash-command body, session overlay, turn
    /// restart notice, active background jobs) into history immediately BEFORE
    /// the most recent real user message, so the real user message stays the
    /// last user-role content the model sees before generating its reply.
    ///
    /// A volatile block placed AFTER the user message is read by strict ChatML
    /// templates (Qwen3) as a fresh user turn: the model restarts its assistant
    /// response, scans back for the last real user content, and re-narrates the
    /// same plan on every tool-loop iteration until context fills — the
    /// production spin observed on D0AC6CKBK5K. Inserting before the user
    /// message keeps the tail as [..., volatile-nudge, real-user, assistant],
    /// which every chat template anchors to correctly.
    ///
    /// Cache-prefix stability (the reason PR #1178 moved volatile context into
    /// history) is preserved: the inserted message's byte position is fixed
    /// once added, and subsequent turns only append, so a byte-prefix-caching
    /// provider extends the cached prefix straight through it — identical
    /// guarantee to the prior append-based placement, only the local order of
    /// the two adjacent turn-start messages differs.
    ///
    /// When the last history entry is NOT a real user message (reminder /
    /// scheduled turn, delivery-retry redrive, cold-recovery), there is no
    /// trailing user message to sit before, so the block is appended.
    /// </summary>
    public SessionState AddVolatileContextNudge(string nudge)
    {
        var nudgeMsg = BuildNudgeMessage(nudge);

        if (History.Count > 0
            && History[^1].Role == ChatRole.User
            && !IsSystemNudge(History[^1]))
        {
            return this with { History = History.Insert(History.Count - 1, nudgeMsg) };
        }

        return this with { History = History.Add(nudgeMsg) };
    }

    private static SerializableChatMessage BuildNudgeMessage(
        string nudge,
        IReadOnlyList<SerializableMediaReference>? mediaReferences = null) =>
        mediaReferences is { Count: > 0 }
            ? new()
            {
                Role = ChatRole.User,
                Content = $"{SystemNudgePrefix} {nudge}]",
                // Snapshot, never alias. The model-input media nudge is built from
                // the caller's media accumulator (ModelInputMediaBuffer.DrainSnapshot),
                // which reuses/empties its backing list across batches.
                // SerializableChatMessage is an immutable persistence type that must
                // own its media list — without this copy the caller's reuse could
                // empty the nudge's attachments before the next LLM call hydrates
                // them, so a tool-loaded image would silently never reach the model.
                MediaReferences = [.. mediaReferences]
            }
            : new() { Role = ChatRole.User, Content = $"{SystemNudgePrefix} {nudge}]" };

    /// <summary>
    /// Find the last user message in history (for building persistence events).
    /// </summary>
    public SerializableChatMessage? FindLastUserMessage()
    {
        for (var i = History.Count - 1; i >= 0; i--)
        {
            var message = History[i];
            if (message.Role != ChatRole.User)
                continue;

            if (IsSystemNudge(message))
                continue;

            return message;
        }

        return null;
    }

    internal static bool IsSystemNudge(SerializableChatMessage message)
    {
        return message.Role == ChatRole.User
            && message.Content.StartsWith(SystemNudgePrefix, StringComparison.Ordinal);
    }

    // ── Compaction helpers ──

    /// <summary>
    /// Phase 1 of compaction: Clear old tool results while preserving recent ones.
    /// Replaces old tool result content with a placeholder while keeping the tool
    /// call structure intact (no orphaned tool calls).
    /// </summary>
    /// <param name="keepRecent">Number of recent tool call/result groups to preserve in full.</param>
    /// <returns>A new state with old tool results cleared, and the count of results that were cleared.</returns>
    public (SessionState State, int ClearedCount) ClearOldToolResults(int keepRecent)
    {
        if (keepRecent < 0) keepRecent = 0;

        // Find all tool result message indices (Role == Tool)
        var toolResultIndices = new List<int>();
        for (var i = 0; i < History.Count; i++)
        {
            if (History[i].Role == ChatRole.Tool)
            {
                toolResultIndices.Add(i);
            }
        }

        if (toolResultIndices.Count <= keepRecent)
        {
            return (this, 0);
        }

        // Clear all but the last N tool results
        var indicesToClear = toolResultIndices
            .Take(toolResultIndices.Count - keepRecent)
            .ToHashSet();

        var builder = History.ToBuilder();
        var clearedCount = 0;

        foreach (var idx in indicesToClear)
        {
            var msg = builder[idx];
            builder[idx] = new SerializableChatMessage
            {
                Role = ChatRole.Tool,
                Content = $"[Tool result cleared — {msg.Name ?? "unknown"} call {msg.ToolCallId?.Value ?? "?"}]",
                ToolCallId = msg.ToolCallId,
                Name = msg.Name
            };
            clearedCount++;
        }

        return (this with { History = builder.ToImmutable() }, clearedCount);
    }

    // ── Snapshot conversion ──

    public SessionSnapshot ToSnapshot()
    {
        return new SessionSnapshot
        {
            History = new List<SerializableChatMessage>(History),
            LoopCheckpoint = LoopCheckpoint, LoopAdmission = LoopAdmission,
            LoopObservations = LoopObservations.ToArray(), LoopReceiptFailure = LoopReceiptFailure,
            AdoptedTaskContext = AdoptedTaskContext, AdoptedTaskInputIds = AdoptedTaskInputIds,
            ChildRuns = ChildRuns.Values.OrderBy(static run => run.AcceptedAtMs).ThenBy(static run => run.RunId.Value, StringComparer.Ordinal).ToArray(),
            ProcessedBackgroundJobIds = ProcessedBackgroundJobIds.ToArray(),
            PendingInputs = PendingInputs.ToArray(),
            RecentSourceMessageKeys = RecentSourceMessageKeys.ToArray(),
            TurnCount = TurnCount,
            Title = Title,
            WorkingContext = WorkingContext.IsEmpty ? null : WorkingContext,
            ActiveBackgroundJobs = [.. ActiveBackgroundJobs.Values],
            AdoptedContextRecords = [.. AdoptedContextRecords.Values
                .OrderBy(record => record.AuthorizedMessageId, StringComparer.Ordinal)
                .Select(record => new SessionSnapshot.AdoptedContextSnapshotRecord
                {
                    AuthorizedMessageId = record.AuthorizedMessageId,
                    AuthorizerSenderId = record.AuthorizerSenderId,
                    LowerBound = record.LowerBound,
                    UpperBound = record.UpperBound,
                    Projection = record.Projection,
                    HasAdoptedContext = record.HasAdoptedContext,
                    HasThirdPartyAdoptedContext = record.HasThirdPartyAdoptedContext,
                    AdoptedSpeakerIds = [.. record.AdoptedSpeakerIds],
                    ProjectionPersisted = record.ProjectionPersisted,
                    Messages = [.. record.Messages
                        .Select(message => new SessionSnapshot.AdoptedContextSnapshotRecord.AdoptedContextSnapshotMessage
                        {
                            MessageId = message.MessageId,
                            SenderId = message.SenderId,
                            TimestampMs = message.Timestamp.ToUnixTimeMilliseconds(),
                            AuthorityAtInclusion = message.AuthorityAtInclusion
                        })]
                })]
        };
    }

    public static SessionState FromSnapshot(SessionSnapshot snapshot)
    {
        foreach (var run in snapshot.ChildRuns)
            run.Validate();
        if (snapshot.ChildRuns.Select(static run => run.StartKey).Distinct().Count() != snapshot.ChildRuns.Count)
            throw new InvalidDataException("A child snapshot repeats an accepted start identity.");
        var activeJobs = snapshot.ActiveBackgroundJobs.Count > 0
            ? snapshot.ActiveBackgroundJobs.ToImmutableDictionary(
                j => $"{Jobs.BackgroundJobManagerActor.JobDeliveryKeyPrefix}{j.JobId}", j => j)
            : [];

        var adoptedContextRecords = snapshot.AdoptedContextRecords.Count > 0
            ? snapshot.AdoptedContextRecords.ToImmutableDictionary(
                record => record.AuthorizedMessageId,
                record => new AdoptedContextAuditRecord(
                    record.AuthorizedMessageId,
                    record.AuthorizerSenderId,
                    record.LowerBound,
                    record.UpperBound,
                    record.Projection,
                    record.HasAdoptedContext,
                    record.HasThirdPartyAdoptedContext,
                    [.. record.AdoptedSpeakerIds],
                    record.ProjectionPersisted,
                    [.. record.Messages
                        .Select(message => new AdoptedContextAuditMessage(
                            message.MessageId,
                            message.SenderId,
                            DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampMs),
                            message.AuthorityAtInclusion))]))
            : [];

        var terminalSequences = snapshot.ChildRuns.Where(static run => run.TerminalSequenceNr is not null)
            .Select(static run => run.TerminalSequenceNr!.Value).ToArray();
        if (terminalSequences.Distinct().Count() != terminalSequences.Length)
            throw new InvalidDataException("A child snapshot repeats a terminal journal sequence.");
        var state = new SessionState
        {
            History = ImmutableList.CreateRange(snapshot.History),
            LoopCheckpoint = snapshot.LoopCheckpoint ?? new ToolLoopCheckpoint(),
            LoopAdmission = snapshot.LoopAdmission,
            LoopObservations = ImmutableList.CreateRange(snapshot.LoopObservations),
            LoopReceiptFailure = snapshot.LoopReceiptFailure,
            AdoptedTaskContext = snapshot.AdoptedTaskContext, AdoptedTaskInputIds = snapshot.AdoptedTaskInputIds,
            ChildRuns = snapshot.ChildRuns.ToImmutableDictionary(static run => run.RunId),
            ProcessedBackgroundJobIds = ImmutableHashSet.CreateRange(snapshot.ProcessedBackgroundJobIds),
            PendingInputs = ImmutableList.CreateRange(snapshot.PendingInputs),
            RecentSourceMessageKeys = ImmutableList.CreateRange(snapshot.RecentSourceMessageKeys),
            TurnCount = snapshot.TurnCount,
            Title = snapshot.Title,
            WorkingContext = snapshot.WorkingContext ?? WorkingContext.Empty,
            ActiveBackgroundJobs = activeJobs,
            AdoptedContextRecords = adoptedContextRecords
        };
        foreach (var input in state.PendingInputs.Where(static input => input.SourceChildRunId is not null))
            _ = state.GetChildContinuation(input);
        if (state.PendingInputs.Where(static input => input.SourceChildRunId is not null)
            .Select(static input => input.InputId).Distinct().Count()
            != state.PendingInputs.Count(static input => input.SourceChildRunId is not null))
            throw new InvalidDataException("A child snapshot repeats a pending delivery input.");
        return state;
    }
}
