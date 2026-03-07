namespace Netclaw.Actors.Channels;

/// <summary>
/// Ephemeral metadata describing where a user message originated.
/// Used for ACL checks and audit logging — NOT persisted with the session.
/// </summary>
public sealed record MessageSource
{
    /// <summary>
    /// Channel type identifier (e.g. "console", "headless", "slack").
    /// </summary>
    public required string ChannelType { get; init; }

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
    /// When the message was received by the channel.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; }
}
