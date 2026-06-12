// -----------------------------------------------------------------------
// <copyright file="MattermostChannelOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Channels.Mattermost;

public sealed class MattermostChannelOptions : IRemoteChatChannelOptions
{
    public bool Enabled { get; init; }

    public string? ServerUrl { get; init; }

    public SensitiveString? BotToken { get; init; }

    /// <summary>
    /// URL that Mattermost can reach to deliver interactive button callbacks.
    /// Required for button-based approval prompts. Falls back to text-only
    /// prompts when not configured.
    /// Example: <c>https://netclaw.example.com/api/mattermost/actions</c>
    /// </summary>
    public string? CallbackUrl { get; init; }

    public string? DefaultChannelId { get; init; }

    public bool AllowDirectMessages { get; init; }

    public bool MentionOnly { get; init; } = true;

    public bool MentionRequiredInDm { get; init; }

    public string[] AllowedChannelIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];

    /// <summary>
    /// Per-channel audience overrides. Keys are Mattermost channel IDs or the
    /// special key <c>"dm"</c> for direct messages. Values are
    /// <c>"personal"</c>, <c>"team"</c>, or <c>"public"</c>.
    /// </summary>
    public Dictionary<string, string> ChannelAudiences { get; init; } = new(StringComparer.Ordinal);
}
