using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Strongly-typed inbound message for the session stream API.
/// Supports multi-modal content via <see cref="AIContent"/> from
/// Microsoft.Extensions.AI.Abstractions.
/// </summary>
public sealed record ChannelInput
{
    /// <summary>
    /// Identity of the user who sent this message.
    /// </summary>
    public required string SenderId { get; init; }

    /// <summary>
    /// Optional channel-specific identifier (e.g. Slack channel ID).
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Optional message ID for correlation and deduplication.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// Optional source audience hint carried from the inbound adapter.
    /// When omitted, the channel pipeline applies strict defaults.
    /// </summary>
    public TrustAudience? Audience { get; init; }

    /// <summary>
    /// Optional trust boundary hint carried from the inbound adapter.
    /// When omitted, the channel pipeline applies adapter defaults.
    /// </summary>
    public string? Boundary { get; init; }

    /// <summary>
    /// Optional principal classification for the sender.
    /// When omitted, the channel pipeline applies strict defaults.
    /// </summary>
    public PrincipalClassification? Principal { get; init; }

    /// <summary>
    /// Provenance markers that distinguish transport verification from content taint.
    /// </summary>
    public SourceProvenance? Provenance { get; init; }

    /// <summary>
    /// Message content. Supports text (<see cref="TextContent"/>),
    /// images, and other modalities via the <see cref="AIContent"/> hierarchy.
    /// </summary>
    public required IReadOnlyList<AIContent> Contents { get; init; }

    /// <summary>
    /// When the message was received by the channel.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; }
}
