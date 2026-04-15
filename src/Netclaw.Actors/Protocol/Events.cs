using ProtoBuf;

namespace Netclaw.Actors.Protocol;

// TODO: delete SystemPromptSet and its Recover<> handler once old journals have been migrated
/// <summary>
/// Retained for backward compatibility with pre-v0.9 journals.
/// No longer persisted — the system prompt is now read fresh from identity files on every recovery.
/// </summary>
[ProtoContract]
[Obsolete("No longer persisted. Retained so old journals can still deserialize during recovery.")]
public sealed class SystemPromptSet
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public string Content { get; set; } = string.Empty;

    [ProtoMember(3)]
    public long SetAtMs { get; set; }

    public DateTimeOffset SetAt => DateTimeOffset.FromUnixTimeMilliseconds(SetAtMs);
}

/// <summary>
/// Persisted event recording a completed turn (user message + assistant reply).
/// </summary>
[ProtoContract]
public sealed class TurnRecorded
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public SerializableChatMessage UserMessage { get; set; } = new();

    [ProtoMember(3)]
    public SerializableChatMessage AssistantReply { get; set; } = new();

    [ProtoMember(4)]
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
    [ProtoMember(5)]
    public string? SourceReminderId { get; set; }

    public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
}

/// <summary>
/// Persisted event recording that the session title was set or updated.
/// </summary>
[ProtoContract]
public sealed class SessionTitleSet
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public string Title { get; set; } = string.Empty;

    [ProtoMember(3)]
    public long SetAtMs { get; set; }

    public DateTimeOffset SetAt => DateTimeOffset.FromUnixTimeMilliseconds(SetAtMs);
}

/// <summary>
/// Persisted event recording that a session's conversation history was compacted.
/// A snapshot is also taken after this event to avoid replaying the full journal.
/// </summary>
[ProtoContract]
public sealed class SessionCompacted
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public string Summary { get; set; } = string.Empty;

    [ProtoMember(3)]
    public List<SerializableChatMessage> CompactedMessages { get; set; } = new();

    [ProtoMember(4)]
    public int TurnCountBefore { get; set; }

    [ProtoMember(5)]
    public long CompactedAtMs { get; set; }

    // ProtoMember(6) reserved — formerly CompactionBoundaryIndex, removed
    // before the compaction-rework change merged. Do not reuse this field
    // number; local dev journals written during the rework development
    // window may still contain an int at position 6, and re-binding it to
    // a different type would fail deserialization silently.

    /// <summary>
    /// Updated working-context state carried on the event so
    /// <see cref="Sessions.SessionState.Apply(SessionCompacted)"/> can
    /// preserve it across compaction. Null means "no update — retain
    /// the existing <see cref="Sessions.WorkingContext"/> unchanged."
    /// </summary>
    [ProtoMember(7)]
    public Sessions.WorkingContext? WorkingContext { get; set; }

    public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
}
