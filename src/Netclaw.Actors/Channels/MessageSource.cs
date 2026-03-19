using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Ephemeral metadata describing where a user message originated.
/// Used for ACL checks and audit logging — NOT persisted with the session.
/// </summary>
public sealed record MessageSource
{
    /// <summary>
    /// Channel type identifier.
    /// </summary>
    public required ChannelType ChannelType { get; init; }

    /// <summary>
    /// Identity of the sender within the channel (e.g. Slack user ID, "local-user").
    /// </summary>
    public required string SenderId { get; init; }

    /// <summary>
    /// Optional channel-specific identifier (e.g. Slack channel ID).
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Optional source message identifier from the inbound transport.
    /// Useful for routing diagnostics and dedup correlation.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// Correlation identifier for this turn. Propagated across session logs
    /// and actor boundaries for end-to-end traceability.
    /// </summary>
    public string? TurnId { get; init; }

    /// <summary>
    /// Effective source audience attached to the inbound message before any runtime
    /// trust-context derivation occurs.
    /// </summary>
    public TrustAudience Audience { get; init; } = TrustAudience.Public;

    /// <summary>
    /// Runtime-owned security boundary used to partition durable memory and other
    /// reusable state across trust domains.
    /// </summary>
    public string Boundary { get; init; } = SecurityPolicyDefaults.PublicBoundary;

    /// <summary>
    /// Principal classification hint for the inbound sender.
    /// </summary>
    public PrincipalClassification Principal { get; init; } = PrincipalClassification.UntrustedExternal;

    /// <summary>
    /// Provenance markers used to separate transport authenticity from payload taint.
    /// </summary>
    public SourceProvenance Provenance { get; init; } = SourceProvenance.StrictDefault();

    /// <summary>
    /// When the message was received by the channel.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; }
}
