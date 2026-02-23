using Microsoft.Extensions.AI;

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
    /// Optional message ID for correlation and deduplication.
    /// </summary>
    public string? MessageId { get; init; }

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
