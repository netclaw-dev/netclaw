// -----------------------------------------------------------------------
// <copyright file="SessionHubLimits.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Size limit for a single SignalR frame on the session hub. The daemon enforces it; the CLI measures the
/// exact frame it is about to send against it (see <c>SessionHubFrame</c>) so an over-limit chat message is
/// refused with a clear error instead of making the server drop the connection.
/// </summary>
public static class SessionHubLimits
{
    /// <summary>Hub <c>MaximumReceiveMessageSize</c>: 512 KB for the whole frame.</summary>
    public const int MaxReceiveMessageBytes = 512 * 1024;
}
