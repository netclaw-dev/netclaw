// -----------------------------------------------------------------------
// <copyright file="ChannelDeliveryTargetInfo.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Tools;

/// <summary>
/// Tool-context-safe snapshot of a resolved channel delivery target.
/// Uses wire strings so lower-level tool abstractions do not depend on channel
/// registry implementation assemblies.
/// </summary>
public sealed record ChannelDeliveryTargetInfo
{
    public ChannelDeliveryTargetInfo(
        string channelKey,
        string destinationKind,
        string destinationId,
        string? destinationDisplayName = null,
        string? threadOrRootId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);

        ChannelKey = channelKey.Trim();
        DestinationKind = destinationKind.Trim();
        DestinationId = destinationId.Trim();
        DestinationDisplayName = string.IsNullOrWhiteSpace(destinationDisplayName)
            ? null
            : destinationDisplayName.Trim();
        ThreadOrRootId = string.IsNullOrWhiteSpace(threadOrRootId)
            ? null
            : threadOrRootId.Trim();
    }

    public string ChannelKey { get; init; }

    public string DestinationKind { get; init; }

    public string DestinationId { get; init; }

    public string? DestinationDisplayName { get; init; }

    public string? ThreadOrRootId { get; init; }
}
