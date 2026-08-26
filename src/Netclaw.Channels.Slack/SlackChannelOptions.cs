// -----------------------------------------------------------------------
// <copyright file="SlackChannelOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Channels.Slack;

public sealed class SlackChannelOptions : IRemoteChatChannelOptions
{
    public bool Enabled { get; init; }

    public bool SocketMode { get; init; } = true;

    public SensitiveString? BotToken { get; init; }

    public SensitiveString? AppToken { get; init; }

    public string? DefaultChannelId { get; init; }

    public string? DefaultChannelName { get; init; }

    public bool MentionOnly { get; init; } = true;

    public bool AllowDirectMessages { get; init; }

    /// <summary>
    /// If true, DMs require a bot mention just like channel messages.
    /// Default is false — DMs are processed without requiring a mention.
    /// Only applies when <see cref="AllowDirectMessages"/> is true.
    /// </summary>
    public bool MentionRequiredInDm { get; init; } = false;

    /// <summary>
    /// Per-channel opt-in for the thread mention rule. Keys are channel IDs.
    /// When a channel's value is <c>true</c>, a thread reply requires a bot
    /// mention even when the thread already has an active session. A channel
    /// with no entry defaults to <c>false</c> — follow-up replies in an active
    /// thread are processed without a mention.
    /// </summary>
    public Dictionary<string, bool> MentionRequiredInThreadByChannel { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves the effective thread mention rule for a channel: the per-channel
    /// value, or <c>false</c> when the channel has no entry.
    /// </summary>
    public bool MentionRequiredInThreadFor(string channelId)
        => MentionRequiredInThreadByChannel.TryGetValue(channelId, out var required) && required;

    public string[] AllowedChannelIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];

    /// <summary>
    /// Per-channel audience overrides. Keys are Slack channel IDs or the special
    /// key <c>"dm"</c> for direct messages. Values are audience strings:
    /// <c>"personal"</c>, <c>"team"</c>, or <c>"public"</c>.
    /// When a channel/DM is not mapped, the existing ACL heuristic applies.
    /// </summary>
    public Dictionary<string, string> ChannelAudiences { get; init; } = new(StringComparer.Ordinal);
}
