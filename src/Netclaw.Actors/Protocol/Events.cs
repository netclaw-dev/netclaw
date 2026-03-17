using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Persisted event recording that the system prompt was set or replaced.
/// This is the first event in a new session's journal.
/// </summary>
[ProtoContract]
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

    [ProtoMember(5)]
    public bool ConsumesBufferedInput { get; set; }

    public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
}

/// <summary>
/// Persisted event recording a completed turn that ended with a user-visible
/// failure reply instead of a normal assistant answer.
/// </summary>
[ProtoContract]
public sealed class TurnFailedRecorded
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public SerializableChatMessage UserMessage { get; set; } = new();

    [ProtoMember(3)]
    public SerializableChatMessage AssistantReply { get; set; } = new();

    [ProtoMember(4)]
    public long RecordedAtMs { get; set; }

    [ProtoMember(5)]
    public bool ConsumesBufferedInput { get; set; }

    [ProtoMember(6)]
    public int ErrorCategory { get; set; }

    public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
}

/// <summary>
/// Persisted event recording a user message accepted while the session is busy.
/// The message is replayed after the active turn reaches a terminal state.
/// </summary>
[ProtoContract]
public sealed class BufferedInputAccepted
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public SerializableChatMessage UserMessage { get; set; } = new();

    [ProtoMember(3)]
    public long AcceptedAtMs { get; set; }

    public DateTimeOffset AcceptedAt => DateTimeOffset.FromUnixTimeMilliseconds(AcceptedAtMs);
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

    public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
}
