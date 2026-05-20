// -----------------------------------------------------------------------
// <copyright file="MattermostRoutingPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Mattermost;

internal static class MattermostRoutingPolicy
{
    public static MattermostRoutingDecision Evaluate(
        MattermostGatewayMessage message,
        bool mentionOnly,
        bool allowDirectMessages,
        bool mentionRequiredInDm,
        bool threadExists,
        bool containsBotMention)
    {
        var hasAttachments = message.Attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(message.Text) && !hasAttachments)
            return MattermostRoutingDecision.Ignore(MattermostRoutingIgnoreReason.NoContent);

        if (message.IsDirectMessage)
        {
            if (!allowDirectMessages)
                return MattermostRoutingDecision.Ignore(MattermostRoutingIgnoreReason.DmNotAllowed);
            if (mentionRequiredInDm && !containsBotMention)
                return MattermostRoutingDecision.Ignore(MattermostRoutingIgnoreReason.DmMentionRequired);
            return MattermostRoutingDecision.StartOrContinue;
        }

        if (threadExists)
            return MattermostRoutingDecision.ContinueOnly;

        // Thread reply where the actor was lost (e.g. daemon restart):
        // the message has a root_id, so re-create the session binding
        // and continue the persisted session.
        if (!message.RootPostId.IsEmpty)
            return MattermostRoutingDecision.StartOrContinue;

        if (!mentionOnly)
            return MattermostRoutingDecision.StartOrContinue;

        return containsBotMention
            ? MattermostRoutingDecision.StartOrContinue
            : MattermostRoutingDecision.Ignore(MattermostRoutingIgnoreReason.ChannelMentionRequired);
    }
}

internal enum MattermostRoutingDecisionKind
{
    Ignore,
    ContinueOnly,
    StartOrContinue
}

internal enum MattermostRoutingIgnoreReason
{
    NoContent,
    DmNotAllowed,
    DmMentionRequired,
    ChannelMentionRequired
}

internal sealed record MattermostRoutingDecision(
    MattermostRoutingDecisionKind Kind,
    MattermostRoutingIgnoreReason? IgnoreReason)
{
    public static readonly MattermostRoutingDecision StartOrContinue =
        new(MattermostRoutingDecisionKind.StartOrContinue, null);

    public static readonly MattermostRoutingDecision ContinueOnly =
        new(MattermostRoutingDecisionKind.ContinueOnly, null);

    public static MattermostRoutingDecision Ignore(MattermostRoutingIgnoreReason reason) =>
        new(MattermostRoutingDecisionKind.Ignore, reason);

    public static string TelemetryLabelFor(MattermostRoutingIgnoreReason reason) =>
        reason switch
        {
            MattermostRoutingIgnoreReason.NoContent => "routing_policy_ignore:NoContent",
            MattermostRoutingIgnoreReason.DmNotAllowed => "routing_policy_ignore:DmNotAllowed",
            MattermostRoutingIgnoreReason.DmMentionRequired => "routing_policy_ignore:DmMentionRequired",
            MattermostRoutingIgnoreReason.ChannelMentionRequired => "routing_policy_ignore:ChannelMentionRequired",
            _ => "routing_policy_ignore",
        };
}
