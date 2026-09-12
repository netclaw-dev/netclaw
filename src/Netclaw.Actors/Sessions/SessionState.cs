// -----------------------------------------------------------------------
// <copyright file="SessionState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
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
public sealed record SessionState
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
    /// In-memory best-effort dedup ledger for background-job-originated turns.
    /// Same pattern as <see cref="ProcessedReminderIds"/> — not persisted to
    /// snapshot, rebuilds from event replay.
    /// </summary>
    public IImmutableSet<BackgroundJobId> ProcessedBackgroundJobIds { get; init; } =
        ImmutableHashSet<BackgroundJobId>.Empty;

    // ── Event application (pure functions) ──

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
    /// behavior mid-turn (empty-response retry, duplicate-tool, budget warning,
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

        return new SessionState
        {
            History = ImmutableList.CreateRange(snapshot.History),
            TurnCount = snapshot.TurnCount,
            Title = snapshot.Title,
            WorkingContext = snapshot.WorkingContext ?? WorkingContext.Empty,
            ActiveBackgroundJobs = activeJobs,
            AdoptedContextRecords = adoptedContextRecords
        };
    }
}
