// -----------------------------------------------------------------------
// <copyright file="DiscordChannelOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Channels.Discord;

public sealed class DiscordChannelOptions
{
    public bool Enabled { get; init; }

    public SensitiveString? BotToken { get; init; }

    public string? DefaultChannelId { get; init; }

    public bool AllowDirectMessages { get; init; }

    public bool MentionOnly { get; init; } = true;

    public bool MentionRequiredInDm { get; init; }

    public string[] AllowedChannelIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];

    /// <summary>
    /// Per-channel audience overrides. Keys are Discord channel IDs or the
    /// special key <c>"dm"</c> for direct messages. Values are
    /// <c>"personal"</c>, <c>"team"</c>, or <c>"public"</c>.
    /// </summary>
    public Dictionary<string, string> ChannelAudiences { get; init; } = new(StringComparer.Ordinal);
}
