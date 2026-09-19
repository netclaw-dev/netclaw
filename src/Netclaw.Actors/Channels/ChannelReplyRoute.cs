// -----------------------------------------------------------------------
// <copyright file="ChannelReplyRoute.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Actors.Channels;

/// <summary>
/// The transport facts that a channel needs to restore the original reply route.
/// The channel still enforces its current ACL before it binds the route.
/// </summary>
public sealed record ChannelReplyRoute(
    bool IsDirectMessage,
    string ReplyChannelId,
    string? RootMessageId = null);
