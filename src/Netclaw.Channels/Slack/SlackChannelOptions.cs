namespace Netclaw.Channels.Slack;

public sealed class SlackChannelOptions
{
    public bool Enabled { get; init; }

    public bool SocketMode { get; init; } = true;

    public string? BotToken { get; init; }

    public string? AppToken { get; init; }

    public string? DefaultChannelId { get; init; }

    public string? DefaultChannelName { get; init; }

    public bool MentionOnly { get; init; } = true;

    public bool AllowDirectMessages { get; init; }

    public string[] AllowedChannelIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];
}
