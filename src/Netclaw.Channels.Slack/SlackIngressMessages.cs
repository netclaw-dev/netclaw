using Netclaw.Actors.Protocol;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using SlackNet.Blocks;

namespace Netclaw.Channels.Slack;

public enum SlackInboundKind
{
    Message,
    AppMention,
    BlockAction
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
    SlackEventId EventId,
    string TurnId,
    string SenderId,
    TrustAudience Audience,
    PrincipalClassification Principal,
    SourceProvenance Provenance,
    string Text,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<SlackFileReference>? Files = null);

public sealed record SlackPostMessage(
    SlackChannelId ChannelId,
    SlackThreadTs ThreadTs,
    string Text,
    IReadOnlyList<Block>? Blocks = null);

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

/// <summary>
/// Routes a tool approval response from the Slack Block Kit button handler
/// back into the Slack actor hierarchy so the thread actor can enforce
/// requester checks and clear pending approval state consistently.
/// </summary>
public sealed record SlackApprovalResponse(
    SlackChannelId ChannelId,
    SlackThreadTs ThreadTs,
    string CallId,
    string SelectedKey,
    string SenderId,
    string? RequesterSenderId = null);
