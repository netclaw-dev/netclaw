namespace Netclaw.Actors.Protocol;

/// <summary>
/// Delivery failure categories a channel adapter can report back to the session.
/// Only retryable categories should be fed back to the LLM.
/// </summary>
public enum DeliveryFailureKind
{
    ContentRejected,
    MessageTooLarge,
    UnsupportedContent,
    PermissionDenied,
    TransportFailure,
    Unknown
}

/// <summary>
/// Sent by a channel adapter when the session's output could not be delivered.
/// The session can use this structured feedback to decide whether to retry.
/// </summary>
public sealed record DeliveryFailed : IWithSessionId
{
    public required SessionId SessionId { get; init; }

    /// <summary>
    /// Completed turn number whose output failed delivery.
    /// Used to reject stale feedback after a newer user turn has started.
    /// </summary>
    public required int TurnNumber { get; init; }

    /// <summary>
    /// Channel adapter identifier (for example: slack, discord, teams).
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Structured reason for the failure.
    /// </summary>
    public required DeliveryFailureKind FailureKind { get; init; }

    /// <summary>
    /// Raw channel error message for operator diagnostics and LLM guidance.
    /// </summary>
    public required string ErrorMessage { get; init; }
}
