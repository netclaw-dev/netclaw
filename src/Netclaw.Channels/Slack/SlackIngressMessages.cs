using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Slack;

public enum SlackInboundKind
{
    Message,
    AppMention
}

public sealed record SlackInboundMessage(
    SlackInboundKind Kind,
    string EventId,
    string ChannelId,
    string? ThreadTs,
    string EventTs,
    string? UserId,
    string? BotId,
    string Text,
    string? Subtype,
    bool Hidden,
    bool IsDirectMessage);

public sealed record SlackThreadInbound(
    SessionId SessionId,
    string ChannelId,
    string ThreadTs,
    string SenderId,
    string Text,
    DateTimeOffset ReceivedAt);

public sealed record SlackPostMessage(
    string ChannelId,
    string ThreadTs,
    string Text);
