// -----------------------------------------------------------------------
// <copyright file="SessionState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;

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
        string? AuthorizerSenderId,
        string? LowerBound,
        string? UpperBound,
        string Projection,
        bool ProjectionPersisted,
        ImmutableList<AdoptedContextAuditMessage> Messages);

    public sealed record AdoptedContextAuditMessage(
        string MessageId,
        string SenderId,
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
    public IImmutableSet<string> ProcessedReminderIds { get; init; } =
        ImmutableHashSet<string>.Empty;

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
    public IImmutableSet<string> ProcessedBackgroundJobIds { get; init; } =
        ImmutableHashSet<string>.Empty;

    // ── Event application (pure functions) ──

    public SessionState Apply(TurnRecorded evt)
    {
        var processedReminders = ProcessedReminderIds;
        if (!string.IsNullOrEmpty(evt.SourceReminderId))
            processedReminders = processedReminders.Add(evt.SourceReminderId);

        var processedJobs = ProcessedBackgroundJobIds;
        var activeJobs = ActiveBackgroundJobs;
        if (!string.IsNullOrEmpty(evt.SourceBackgroundJobId))
        {
            processedJobs = processedJobs.Add(evt.SourceBackgroundJobId);
            activeJobs = activeJobs.Remove(evt.SourceBackgroundJobId);
        }

        return this with
        {
            History = History.Add(evt.UserMessage).Add(evt.AssistantReply),
            TurnCount = TurnCount + 1,
            ProcessedReminderIds = processedReminders,
            ProcessedBackgroundJobIds = processedJobs,
            ActiveBackgroundJobs = activeJobs
        };
    }

    public SessionState Apply(SessionTitleSet evt)
    {
        return this with { Title = evt.Title };
    }

    public SessionState Apply(AdoptedContextRecorded evt)
    {
        var record = new AdoptedContextAuditRecord(
            evt.AuthorizedMessageId,
            evt.AuthorizerSenderId,
            evt.LowerBound,
            evt.UpperBound,
            evt.Projection,
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

    // ── Command helpers ──

    /// <summary>
    /// Add a user message to history (before firing an LLM call).
    /// This is transient state that gets persisted as part of <see cref="TurnRecorded"/>.
    /// </summary>
    public SessionState AddUserMessage(string content, List<SerializableMediaReference>? mediaReferences = null)
    {
        var msg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = content
        };

        if (mediaReferences is { Count: > 0 })
            msg.MediaReferences = mediaReferences;

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
    /// Add a transient system nudge to history to correct LLM behavior (e.g., empty response recovery).
    /// Not persisted as a turn — just injected into the conversation to guide the next LLM call.
    /// </summary>
    public SessionState AddSystemNudge(string nudge)
    {
        return this with
        {
            History = History.Add(new SerializableChatMessage
            {
                Role = ChatRole.User,
                Content = $"{SystemNudgePrefix} {nudge}]"
            })
        };
    }

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
                Content = $"[Tool result cleared — {msg.Name ?? "unknown"} call {msg.ToolCallId ?? "?"}]",
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
