// -----------------------------------------------------------------------
// <copyright file="ChannelOptionsBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Tests.Channels.TestHelpers;

public sealed record ChannelOptionsBuilder
{
    public bool AllowDirectMessages { get; init; }
    public string[] AllowedChannelIds { get; init; } = [];
    public string[] AllowedUserIds { get; init; } = [];
    public Dictionary<string, string> ChannelAudiences { get; init; } = [];
    public string? DefaultChannelId { get; init; }
}
