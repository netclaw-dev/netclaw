// -----------------------------------------------------------------------
// <copyright file="Events.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Serialization;
using Netclaw.Configuration;
using Netclaw.Tools;

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

/// <summary>
/// Persisted event recording an assistant tool-call batch before any tool is
/// executed or approval prompt is emitted. This makes in-flight tool history
/// replayable from the journal instead of relying on snapshots to carry
/// unjournaled current-turn state.
/// </summary>
public sealed record ToolBatchStarted : INetclawSerializableMessage
{
    public SessionId SessionId { get; init; }

    public SerializableChatMessage UserMessage { get; init; } = new();

    public SerializableChatMessage AssistantMessage { get; init; } = new();

    public long StartedAtMs { get; init; }
}

/// <summary>
/// Persisted event recording a single tool result as soon as it completes.
/// This lets recovery avoid re-running completed sibling calls when another
/// call in the same assistant batch is still pending approval.
/// </summary>
public sealed record ToolCallRecorded : INetclawSerializableMessage
{
    public SessionId SessionId { get; init; }

    public SerializableChatMessage ToolResult { get; init; } = new();

    public long RecordedAtMs { get; init; }
}

public sealed record TurnContextRecord
{
    public SessionId SessionId { get; init; }

    public string TurnId { get; init; } = string.Empty;

    public TrustAudience Audience { get; init; }

    public TrustBoundary? Boundary { get; init; }

    public string? ChannelType { get; init; }

    public SenderId? RequesterSenderId { get; init; }

    public PrincipalClassification? RequesterPrincipal { get; init; }

    public TransportAuthenticity TransportAuthenticity { get; init; }

    public PayloadTaint PayloadTaint { get; init; }

    public string? SourceScope { get; init; }

    public string? SourceKind { get; init; }

    public ChannelDeliveryTargetInfo? DefaultDeliveryTarget { get; init; }

    public ChannelDeliveryTargetInfo? RequestedDeliveryTarget { get; init; }

    public bool HasAdoptedContext { get; init; }

    public bool HasThirdPartyAdoptedContext { get; init; }

    public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = Array.Empty<string>();

    public bool SupportsInteractiveApproval { get; init; }
}

public sealed record ToolApprovalRequested : INetclawSerializableMessage
{
    public sealed record ApprovalCandidateRecord
    {
        public string Verb { get; init; } = string.Empty;

        public string? Directory { get; init; }
    }

    public SessionId SessionId { get; init; }

    public string CallId { get; init; } = string.Empty;

    public string ToolName { get; init; } = string.Empty;

    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CandidateVerbs { get; init; } = Array.Empty<string>();

    public TrustAudience Audience { get; init; }

    public TrustBoundary? Boundary { get; init; }

    public string? ChannelType { get; init; }

    public bool? SupportsInteractiveApproval { get; init; }

    public SenderId? RequesterSenderId { get; init; }

    public PrincipalClassification? RequesterPrincipal { get; init; }

    public bool HasThirdPartyAdoptedContext { get; init; }

    public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = Array.Empty<string>();

    public string? Cwd { get; init; }

    public IReadOnlyList<string> OptionKeys { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ApprovalCandidateRecord> Candidates { get; init; } =
        Array.Empty<ApprovalCandidateRecord>();

    public TurnContextRecord? TurnContext { get; init; }

    public long RequestedAtMs { get; init; }
}

public sealed record ToolApprovalResolved : INetclawSerializableMessage
{
    public SessionId SessionId { get; init; }

    public string CallId { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;

    public long ResolvedAtMs { get; init; }
}

public sealed record ToolBatchAbandoned : INetclawSerializableMessage
{
    public SessionId SessionId { get; init; }

    public IReadOnlyList<SerializableChatMessage> ToolResults { get; init; } =
        Array.Empty<SerializableChatMessage>();

    public long AbandonedAtMs { get; init; }
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
