// -----------------------------------------------------------------------
// <copyright file="Events.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Persisted event recording a completed turn (user message + assistant reply).
/// </summary>
public sealed class TurnRecorded
{
    public SessionId SessionId { get; set; }

    public SerializableChatMessage UserMessage { get; set; } = new();

    public SerializableChatMessage AssistantReply { get; set; } = new();

    public long RecordedAtMs { get; set; }

    /// <summary>
    /// Populated when this turn originated from a reminder firing.
    /// Format is <c>"{reminderId}:{fireTimestampMs}"</c>, matching the value
    /// placed on <see cref="Channels.MessageSource.ReminderId"/> by the
    /// reminder dispatcher. Null for regular user turns. Used for forensics
    /// and to rebuild the in-memory reminder dedup ledger
    /// (<see cref="Sessions.SessionState.ProcessedReminderIds"/>) from
    /// event replay.
    /// </summary>
    public string? SourceReminderId { get; set; }

    /// <summary>
    /// Populated when this turn originated from a background job result delivery.
    /// Format is <c>"bg-job:{jobId}"</c>, matching the value placed on
    /// <see cref="Channels.MessageSource.BackgroundJobId"/> by the
    /// background job manager. Null for regular user turns.
    /// </summary>
    public string? SourceBackgroundJobId { get; set; }

    public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
}

public sealed class AdoptedContextRecorded
{
    public sealed class AdoptedMessageRecord
    {
        public string MessageId { get; set; } = string.Empty;

        public string SenderId { get; set; } = string.Empty;

        public long TimestampMs { get; set; }

        public string AuthorityAtInclusion { get; set; } = string.Empty;
    }

    public SessionId SessionId { get; set; }

    public string AuthorizedMessageId { get; set; } = string.Empty;

    public string? AuthorizerSenderId { get; set; }

    public string? LowerBound { get; set; }

    public string? UpperBound { get; set; }

    public string Projection { get; set; } = string.Empty;

    public List<AdoptedMessageRecord> Messages { get; set; } = [];

    public bool ProjectionPersisted { get; set; }

    public long RecordedAtMs { get; set; }

    public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
}

/// <summary>
/// Persisted event recording that the session title was set or updated.
/// </summary>
public sealed class SessionTitleSet
{
    public SessionId SessionId { get; set; }

    public string Title { get; set; } = string.Empty;

    public long SetAtMs { get; set; }

    public DateTimeOffset SetAt => DateTimeOffset.FromUnixTimeMilliseconds(SetAtMs);
}

/// <summary>
/// Persisted event recording that a session's conversation history was compacted.
/// A snapshot is also taken after this event to avoid replaying the full journal.
/// </summary>
public sealed class SessionCompacted
{
    public SessionId SessionId { get; set; }

    public string Summary { get; set; } = string.Empty;

    public List<SerializableChatMessage> CompactedMessages { get; set; } = [];

    public int TurnCountBefore { get; set; }

    public long CompactedAtMs { get; set; }

    /// <summary>
    /// Updated working-context state carried on the event so
    /// <see cref="Sessions.SessionState.Apply(SessionCompacted)"/> can
    /// preserve it across compaction. Null means "no update — retain
    /// the existing <see cref="Sessions.WorkingContext"/> unchanged."
    /// </summary>
    public Sessions.WorkingContext? WorkingContext { get; set; }

    public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
}
