using Netclaw.Configuration;

namespace Netclaw.Channels.Slack;

public sealed class SlackChannelOptions
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

    public string[] AllowedChannelIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];
}
