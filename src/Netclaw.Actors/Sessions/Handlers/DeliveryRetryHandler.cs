// -----------------------------------------------------------------------
// <copyright file="DeliveryRetryHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Manages delivery retry state: eligibility tracking, retry counting,
/// and static helper methods for building nudge messages.
/// </summary>
internal sealed class DeliveryRetryHandler
{
    public const int MaxRetries = 2;

    private int? _eligibleTurnNumber;
    private int _retryCount;
    private bool _chainActive;

    public int? EligibleTurnNumber => _eligibleTurnNumber;
    public int RetryCount => _retryCount;
    public bool ChainActive => _chainActive;

    /// <summary>
    /// Clear all delivery retry state. Called at the start of a new user turn
    /// and when a turn fails.
    /// </summary>
    public void Clear()
    {
        _eligibleTurnNumber = null;
        _retryCount = 0;
        _chainActive = false;
    }

    /// <summary>
    /// Mark the given turn as eligible for delivery retries.
    /// </summary>
    public void MarkEligible(int turnNumber)
    {
        _eligibleTurnNumber = turnNumber;
        if (!_chainActive)
            _retryCount = 0;
    }

    /// <summary>
    /// Record a retry attempt. Sets the chain active flag.
    /// </summary>
    public void RecordRetry()
    {
        _retryCount++;
        _chainActive = true;
    }

    /// <summary>
    /// Exhaust the retry budget for the current turn (marks ineligible).
    /// </summary>
    public void Exhaust()
    {
        _eligibleTurnNumber = null;
        _chainActive = false;
    }

    /// <summary>
    /// Check whether the given delivery failure is retryable.
    /// </summary>
    public static bool IsRetryable(DeliveryFailed msg)
        => msg.FailureKind is DeliveryFailureKind.ContentRejected
            or DeliveryFailureKind.MessageTooLarge
            or DeliveryFailureKind.UnsupportedContent;

    /// <summary>
    /// Build a nudge message describing the delivery failure for LLM context.
    /// </summary>
    public static string BuildNudge(DeliveryFailed msg)
    {
        var guidance = msg.FailureKind switch
        {
            DeliveryFailureKind.ContentRejected => "Produce a simpler channel-safe response and avoid the content pattern the channel rejected.",
            DeliveryFailureKind.MessageTooLarge => "Produce a shorter response that fits the channel's length limits.",
            DeliveryFailureKind.UnsupportedContent => "Avoid unsupported formatting or content types for this channel.",
            DeliveryFailureKind.TransportFailure => "The channel experienced a transport error. Your response content was likely fine. Acknowledge to the user that delivery failed due to a technical issue and offer to retry.",
            DeliveryFailureKind.PermissionDenied => "The bot lacks permission to post in this channel. Inform the user that a permissions issue prevented delivery.",
            DeliveryFailureKind.Unknown or _ => "An unknown delivery error occurred. Acknowledge the issue to the user."
        };

        return $"Your last response could not be delivered to the user via {msg.ChannelType}. "
            + $"The user did not receive it. Delivery failure kind: {msg.FailureKind}. "
            + $"Channel error: {msg.ErrorMessage}\n{guidance}";
    }
}
