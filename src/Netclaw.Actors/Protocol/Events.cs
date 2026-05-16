// -----------------------------------------------------------------------
// <copyright file="Events.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Serialization;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Persisted event recording a completed turn (user message + assistant reply).
/// </summary>
public sealed record TurnRecorded : INetclawSerializableMessage
{
    public SessionId SessionId { get; init; }

    public SerializableChatMessage UserMessage { get; init; } = new();

    public SerializableChatMessage AssistantReply { get; init; } = new();

    public long RecordedAtMs { get; init; }

    /// <summary>
    /// Populated when this turn originated from a reminder firing.
    /// Format is <c>"{reminderId}:{fireTimestampMs}"</c>, matching the value
    /// placed on <see cref="Channels.MessageSource.ReminderId"/> by the
    /// reminder dispatcher. Null for regular user turns. Used for forensics
    /// and to rebuild the in-memory reminder dedup ledger
    /// (<see cref="Sessions.SessionState.ProcessedReminderIds"/>) from
    /// event replay.
    /// </summary>
    public string? SourceReminderId { get; init; }

    /// <summary>
    /// Populated when this turn originated from a background job result delivery.
    /// Format is <c>"bg-job:{jobId}"</c>, matching the value placed on
    /// <see cref="Channels.MessageSource.BackgroundJobId"/> by the
    /// background job manager. Null for regular user turns.
    /// </summary>
    public string? SourceBackgroundJobId { get; init; }

    public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
}

public sealed record AdoptedContextRecorded : INetclawSerializableMessage
{
    public sealed record AdoptedMessageRecord
    {
        public string MessageId { get; init; } = string.Empty;

        public SenderId SenderId { get; init; } = new(string.Empty);

        public long TimestampMs { get; init; }

        public string AuthorityAtInclusion { get; init; } = string.Empty;
    }

    public SessionId SessionId { get; init; }

    public string AuthorizedMessageId { get; init; } = string.Empty;

    public SenderId? AuthorizerSenderId { get; init; }

    public string? LowerBound { get; init; }

    public string? UpperBound { get; init; }

    public string Projection { get; init; } = string.Empty;

    public bool HasAdoptedContext { get; init; }

    public bool HasThirdPartyAdoptedContext { get; init; }

    public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<AdoptedMessageRecord> Messages { get; init; } = Array.Empty<AdoptedMessageRecord>();

    public bool ProjectionPersisted { get; init; }

    public long RecordedAtMs { get; init; }

    public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
}

/// <summary>
/// Persisted event recording that the session title was set or updated.
/// </summary>
public sealed record SessionTitleSet : INetclawSerializableMessage
{
    public SessionId SessionId { get; init; }

    public string Title { get; init; } = string.Empty;

    public long SetAtMs { get; init; }

    public DateTimeOffset SetAt => DateTimeOffset.FromUnixTimeMilliseconds(SetAtMs);
}

/// <summary>
/// Persisted event recording that a session's conversation history was compacted.
/// A snapshot is also taken after this event to avoid replaying the full journal.
/// </summary>
public sealed record SessionCompacted : INetclawSerializableMessage
{
    public SessionId SessionId { get; init; }

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<SerializableChatMessage> CompactedMessages { get; init; } =
        Array.Empty<SerializableChatMessage>();

    public int TurnCountBefore { get; init; }

    public long CompactedAtMs { get; init; }

    /// <summary>
    /// Updated working-context state carried on the event so
    /// <see cref="Sessions.SessionState.Apply(SessionCompacted)"/> can
    /// preserve it across compaction. Null means "no update — retain
    /// the existing <see cref="Sessions.WorkingContext"/> unchanged."
    /// </summary>
    public Sessions.WorkingContext? WorkingContext { get; init; }

    public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
}
