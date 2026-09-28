// -----------------------------------------------------------------------
// <copyright file="MattermostRoutingPolicyContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Mattermost;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class MattermostRoutingPolicyContractTests : DmGatedRoutingPolicyContractTests
{
    protected override RoutingVerdict EvaluateRouting(
        bool mentionOnly, bool isDm, bool containsMention, string text)
        => EvaluateMattermost(
            mentionOnly, allowDm: true, mentionRequiredInDm: false,
            mentionRequiredInThread: false, isDm, containsMention,
            threadExists: false, isThreadReply: false, text);

    protected override RoutingVerdict EvaluateThreadReply(
        bool mentionRequiredInThread, bool containsMention, bool threadExists,
        bool isThreadReply, string text)
        => EvaluateMattermost(
            mentionOnly: true, allowDm: false, mentionRequiredInDm: false,
            mentionRequiredInThread, isDm: false, containsMention,
            threadExists, isThreadReply, text);

    protected override RoutingVerdict EvaluateDmGating(
        bool allowDm, bool mentionRequiredInDm, bool containsMention)
        => EvaluateMattermost(
            mentionOnly: true, allowDm, mentionRequiredInDm,
            mentionRequiredInThread: false, isDm: true, containsMention,
            threadExists: false, isThreadReply: false, "hey");

    private static RoutingVerdict EvaluateMattermost(
        bool mentionOnly,
        bool allowDm,
        bool mentionRequiredInDm,
        bool mentionRequiredInThread,
        bool isDm,
        bool containsMention,
        bool threadExists,
        bool isThreadReply,
        string text)
    {
        // Mattermost recognizes thread replies via a non-empty RootPostId;
        // an empty RootPostId means a top-level channel post.
        var message = new MattermostGatewayMessage(
            EventId: new MattermostEventId("ev-1"),
            ChannelId: new MattermostChannelId(isDm ? "dm-ch-1" : "ch-1"),
            PostId: new MattermostPostId("post-1"),
            RootPostId: new MattermostRootPostId(isThreadReply ? "rootpost123456789012345678" : string.Empty),
            SenderId: new MattermostUserId("u-1"),
            IsBotMessage: false,
            IsDirectMessage: isDm,
            ContainsBotMention: containsMention,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());

        var decision = MattermostRoutingPolicy.Evaluate(
            message, mentionOnly, allowDm, mentionRequiredInDm, mentionRequiredInThread, threadExists, containsMention);

        var kind = decision.Kind switch
        {
            MattermostRoutingDecisionKind.StartOrContinue => RoutingVerdictKind.Route,
            MattermostRoutingDecisionKind.ContinueOnly => RoutingVerdictKind.ContinueOnly,
            MattermostRoutingDecisionKind.Ignore => RoutingVerdictKind.Ignore,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision), decision.Kind, "Unmapped Mattermost routing decision kind.")
        };

        var reason = decision.IgnoreReason switch
        {
            null => (RoutingIgnoreReason?)null,
            MattermostRoutingIgnoreReason.NoContent => RoutingIgnoreReason.NoContent,
            MattermostRoutingIgnoreReason.DmNotAllowed => RoutingIgnoreReason.DmNotAllowed,
            MattermostRoutingIgnoreReason.DmMentionRequired => RoutingIgnoreReason.DmMentionRequired,
            MattermostRoutingIgnoreReason.ThreadMentionRequired => RoutingIgnoreReason.ThreadMentionRequired,
            MattermostRoutingIgnoreReason.ChannelMentionRequired => RoutingIgnoreReason.ChannelMentionRequired,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision), decision.IgnoreReason, "Unmapped Mattermost routing ignore reason.")
        };

        return new RoutingVerdict(kind, reason);
    }
}
