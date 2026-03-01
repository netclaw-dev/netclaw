using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Slack;

public enum SlackInboundKind
{
    Message,
    AppMention
}

public sealed record SlackFileReference(
    string Id,
    string Name,
    string MimeType,
    long Size,
    string UrlPrivateDownload);

public sealed record SlackInboundMessage(
    SlackInboundKind Kind,
    SlackEventId EventId,
    SlackChannelId ChannelId,
    SlackThreadTs? ThreadTs,
    SlackEventTs EventTs,
    SlackUserId? UserId,
    SlackBotId? BotId,
    string Text,
    string? Subtype,
    bool Hidden,
    bool IsDirectMessage,
    IReadOnlyList<SlackFileReference>? Files = null);

public sealed record SlackThreadInbound(
    SessionId SessionId,
    SlackChannelId ChannelId,
    SlackThreadTs ThreadTs,
    string SenderId,
    string Text,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<SlackFileReference>? Files = null);

public sealed record SlackPostMessage(
    SlackChannelId ChannelId,
    SlackThreadTs ThreadTs,
    string Text);
