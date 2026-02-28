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
    string EventId,
    string ChannelId,
    string? ThreadTs,
    string EventTs,
    string? UserId,
    string? BotId,
    string Text,
    string? Subtype,
    bool Hidden,
    bool IsDirectMessage,
    IReadOnlyList<SlackFileReference>? Files = null);

public sealed record SlackThreadInbound(
    SessionId SessionId,
    string ChannelId,
    string ThreadTs,
    string SenderId,
    string Text,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<SlackFileReference>? Files = null);

public sealed record SlackPostMessage(
    string ChannelId,
    string ThreadTs,
    string Text);
