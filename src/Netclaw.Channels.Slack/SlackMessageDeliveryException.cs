using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Raised when Slack rejects message content after the adapter attempted delivery.
/// </summary>
public sealed class SlackMessageDeliveryException : Exception
{
    public SlackMessageDeliveryException(
        string? errorCode,
        DeliveryFailureKind failureKind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        FailureKind = failureKind;
    }

    public string? ErrorCode { get; }

    public DeliveryFailureKind FailureKind { get; }
}
