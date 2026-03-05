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

/// <summary>
/// Sent to the gateway to wire up the actor hierarchy for a proactively-created thread.
/// The Slack message has already been posted; this creates the session pipeline so
/// user replies route back to a live session.
/// </summary>
public sealed record StartProactiveThread(
    SlackChannelId ChannelId,
    SlackThreadTs ThreadTs,
    SessionId SessionId);

/// <summary>
/// Acknowledgement that the proactive thread's session pipeline was initialized.
/// Returned by <see cref="SlackThreadBindingActor"/> in response to <see cref="StartProactiveThread"/>.
/// </summary>
public sealed record ProactiveThreadAck(SessionId SessionId);
