namespace Netclaw.Actors.Protocol;

/// <summary>
/// Sent by a channel adapter to the session actor when output delivery
/// to the end user has failed. The session injects the error details into
/// the conversation and re-invokes the LLM so it can produce corrected output.
/// Routes to the correct session via <see cref="IWithSessionId"/>.
/// Not persisted — triggers a transient system nudge + LLM retry.
/// </summary>
public sealed record DeliveryFailed : IWithSessionId
{
    public required SessionId SessionId { get; init; }

    /// <summary>
    /// Turn number that failed delivery, for correlation.
    /// </summary>
    public required int TurnNumber { get; init; }

    /// <summary>
    /// Channel type that experienced the failure (e.g. "slack", "teams").
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// The full error message from the channel, passed directly to the LLM
    /// so it can understand what went wrong and adjust its output.
    /// </summary>
    public required string ErrorMessage { get; init; }
}
