// -----------------------------------------------------------------------
// <copyright file="DiscordRoutingPolicyContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Discord;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class DiscordRoutingPolicyContractTests : RoutingPolicyContractTests
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
        // Discord recognizes thread replies via IsInThread on the gateway message.
        var message = new DiscordGatewayMessage(
            EventId: new DiscordEventId("ev-1"),
            ChannelId: new DiscordChannelId(isDm ? "dm-1" : "ch-1"),
            ReplyChannelId: new DiscordReplyChannelId(isThreadReply ? "thread-ch-1" : "ch-1"),
            MessageId: new DiscordMessageId("m-1"),
            ThreadOrMessageId: new DiscordThreadOrMessageId(isThreadReply ? "thread-ch-1" : "m-1"),
            RootMessageId: null,
            SenderId: new DiscordUserId("u-1"),
            IsBotMessage: false,
            IsDirectMessage: isDm,
            ContainsBotMention: containsMention,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow(),
            IsInThread: isThreadReply);

        var decision = DiscordRoutingPolicy.Evaluate(
            message, mentionOnly, allowDm, mentionRequiredInDm, threadExists, containsMention);

        var kind = decision.Kind switch
        {
            DiscordRoutingDecisionKind.StartOrContinue => RoutingVerdictKind.Route,
            DiscordRoutingDecisionKind.ContinueOnly => RoutingVerdictKind.ContinueOnly,
            DiscordRoutingDecisionKind.Ignore => RoutingVerdictKind.Ignore,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision), decision.Kind, "Unmapped Discord routing decision kind.")
        };

        var reason = decision.IgnoreReason switch
        {
            null => (RoutingIgnoreReason?)null,
            DiscordRoutingIgnoreReason.NoContent => RoutingIgnoreReason.NoContent,
            DiscordRoutingIgnoreReason.DmNotAllowed => RoutingIgnoreReason.DmNotAllowed,
            DiscordRoutingIgnoreReason.DmMentionRequired => RoutingIgnoreReason.DmMentionRequired,
            DiscordRoutingIgnoreReason.ChannelMentionRequired => RoutingIgnoreReason.ChannelMentionRequired,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision), decision.IgnoreReason, "Unmapped Discord routing ignore reason.")
        };

        return new RoutingVerdict(kind, reason);
    }
}
