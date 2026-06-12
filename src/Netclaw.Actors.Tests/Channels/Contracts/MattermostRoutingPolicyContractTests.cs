// -----------------------------------------------------------------------
// <copyright file="MattermostRoutingPolicyContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Mattermost;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class MattermostRoutingPolicyContractTests : RoutingPolicyContractTests
{
    protected override RoutingVerdict Evaluate(
        bool mentionOnly,
        bool allowDm,
        bool mentionRequiredInDm,
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
            message, mentionOnly, allowDm, mentionRequiredInDm, threadExists, containsMention);

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
            MattermostRoutingIgnoreReason.ChannelMentionRequired => RoutingIgnoreReason.ChannelMentionRequired,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision), decision.IgnoreReason, "Unmapped Mattermost routing ignore reason.")
        };

        return new RoutingVerdict(kind, reason);
    }
}
