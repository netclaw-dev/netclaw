// -----------------------------------------------------------------------
// <copyright file="TeamsPersistenceContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Marks Team-owned durable records. It has its own serializer binding so the
/// generic Actors serializer never writes Teams state.
/// </summary>
public interface ITeamsPersistenceMessage
{
}

public sealed record TeamsApprovalPendingCreated : ITeamsPersistenceMessage
{
    public string CallId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string NonceHash { get; init; } = string.Empty;
    public string? RequesterSenderId { get; init; }
    public PrincipalClassification? RequesterPrincipal { get; init; }
    public long ExpiresAtUnixMilliseconds { get; init; }
    public IReadOnlyList<string> OfferedOptionKeys { get; init; } = Array.Empty<string>();
    public bool IsMcpTool { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string RequestDisplayText { get; init; } = string.Empty;
    /// <summary>
    /// True until Teams confirms that it accepted a card presentation. Recovery
    /// replaces the nonce binding before it attempts another presentation.
    /// </summary>
    public bool PresentationPending { get; init; } = true;
}

public sealed record TeamsApprovalCardDelivered : ITeamsPersistenceMessage
{
    public string CorrelationId { get; init; } = string.Empty;
    /// <summary>
    /// Teams may accept a card without returning an activity ID. A null value
    /// records that supported unbound success without retaining a locator.
    /// </summary>
    public string? PromptId { get; init; }
}

/// <summary>
/// Replaces an expired or ambiguously delivered Adaptive Card binding while
/// leaving the session-owned approval request pending. The raw nonce never
/// enters durable state.
/// </summary>
public sealed record TeamsApprovalCardReissued : ITeamsPersistenceMessage
{
    public string CorrelationId { get; init; } = string.Empty;
    public string NonceHash { get; init; } = string.Empty;
    public long ExpiresAtUnixMilliseconds { get; init; }
}

/// <summary>
/// Records a validated card selection that must be re-driven to the
/// session-authoritative approval handler. It is not a grant or a terminal
/// decision: terminal consumption is written only after the session responds.
/// </summary>
public sealed record TeamsApprovalForwardingStarted : ITeamsPersistenceMessage
{
    public string CorrelationId { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public string SenderId { get; init; } = string.Empty;
    public long StartedAtUnixMilliseconds { get; init; }
}

public sealed record TeamsApprovalConsumed : ITeamsPersistenceMessage
{
    public string CorrelationId { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public long ConsumedAtUnixMilliseconds { get; init; }
}

public sealed record TeamsApprovalSnapshotEntry
{
    public string CallId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string NonceHash { get; init; } = string.Empty;
    public string? RequesterSenderId { get; init; }
    public PrincipalClassification? RequesterPrincipal { get; init; }
    public long ExpiresAtUnixMilliseconds { get; init; }
    public IReadOnlyList<string> OfferedOptionKeys { get; init; } = Array.Empty<string>();
    public bool IsMcpTool { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string RequestDisplayText { get; init; } = string.Empty;
    public string? PromptId { get; init; }
    public bool PresentationPending { get; init; } = true;
    public string? ForwardingDecision { get; init; }
    public string? ForwardingSenderId { get; init; }
    public string? Decision { get; init; }
}

/// <summary>
/// Captures one validated, non-secret outbound destination. Generation starts
/// at one and changes only when a different destination refreshes it.
/// </summary>
public sealed record TeamsProactiveDestinationCaptured : ITeamsPersistenceMessage
{
    public string TenantId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public int Scope { get; init; }
    public string ServiceUrl { get; init; } = string.Empty;
    public string? RootActivityId { get; init; }
    public string? TeamId { get; init; }
    public string? ChannelId { get; init; }
    public string? UserId { get; init; }
    public long Generation { get; init; }
}

/// <summary>
/// Records one delivery transition. A permanent failure may atomically
/// invalidate exactly <see cref="DestinationGeneration"/>.
/// </summary>
public sealed record TeamsProactiveDeliveryRecorded : ITeamsPersistenceMessage
{
    public string DeliveryKey { get; init; } = string.Empty;
    public int State { get; init; }
    public string? EvictedDeliveryKey { get; init; }
    public long DestinationGeneration { get; init; }
    public bool InvalidatesDestination { get; init; }
}

/// <summary>
/// Team-owned compaction state. Migration version one is the first
/// dual-read/new-write-only snapshot format.
/// </summary>
public sealed record TeamsBindingSnapshot(
    IReadOnlyList<string> ActivityFingerprints) : ITeamsPersistenceMessage
{
    public const int CurrentMigrationVersion = 1;
    public IReadOnlyList<TeamsApprovalSnapshotEntry> Approvals { get; init; } = Array.Empty<TeamsApprovalSnapshotEntry>();
    public TeamsProactiveDestinationCaptured? Destination { get; init; }
    public long LastDestinationGeneration { get; init; }
    public IReadOnlyList<TeamsProactiveDeliveryRecorded> ProactiveDeliveries { get; init; } = Array.Empty<TeamsProactiveDeliveryRecorded>();
    public int MigrationVersion { get; init; } = CurrentMigrationVersion;
}

public sealed record TeamsChannelActivityMapped(
    string ActivityFingerprint,
    string SessionId,
    string? EvictedActivityFingerprint,
    string? SenderFingerprint = null) : ITeamsPersistenceMessage;

public sealed record TeamsChannelActivityIndexSnapshot(
    IReadOnlyList<TeamsChannelActivityMapped> Entries) : ITeamsPersistenceMessage;
