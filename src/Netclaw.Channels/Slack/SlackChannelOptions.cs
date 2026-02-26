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

    public string[] AllowedChannelIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];
}
