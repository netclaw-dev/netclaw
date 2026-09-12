// -----------------------------------------------------------------------
// <copyright file="SlackMessageDeliveryException.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
