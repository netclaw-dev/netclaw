// -----------------------------------------------------------------------
// <copyright file="SlackRoutingPolicyContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Slack;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class SlackRoutingPolicyContractTests : RoutingPolicyContractTests
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
        // A thread reply carries a ThreadTs different from its own EventTs —
        // that difference is how SlackRoutingPolicy recognizes replies.
        var message = new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("C0:1"),
            ChannelId: new SlackChannelId(isDm ? "D0" : "C0"),
            ThreadTs: isThreadReply ? new SlackThreadTs("1740468105.120900") : null,
            EventTs: new SlackEventTs("1740468000.000001"),
            UserId: new SlackUserId("U123"),
            BotId: null,
            Text: text,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: isDm,
            Files: null);

        var decision = SlackRoutingPolicy.Evaluate(
            message, mentionOnly, allowDm, mentionRequiredInDm, threadExists, containsMention);

        var kind = decision.Kind switch
        {
            SlackRoutingDecisionKind.StartOrContinue => RoutingVerdictKind.Route,
            SlackRoutingDecisionKind.ContinueOnly => RoutingVerdictKind.ContinueOnly,
            SlackRoutingDecisionKind.Ignore => RoutingVerdictKind.Ignore,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision), decision.Kind, "Unmapped Slack routing decision kind.")
        };

        // WrongKind / HiddenMessage / UnsupportedSubtype cannot arise from the
        // plain Message-kind, subtype-less, non-hidden messages this contract
        // constructs; those reasons are covered by SlackRoutingPolicyTests.
        var reason = decision.IgnoreReason switch
        {
            null => (RoutingIgnoreReason?)null,
            SlackRoutingIgnoreReason.NoContent => RoutingIgnoreReason.NoContent,
            SlackRoutingIgnoreReason.DmNotAllowed => RoutingIgnoreReason.DmNotAllowed,
            SlackRoutingIgnoreReason.DmMentionRequired => RoutingIgnoreReason.DmMentionRequired,
            SlackRoutingIgnoreReason.ChannelMentionRequired => RoutingIgnoreReason.ChannelMentionRequired,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision), decision.IgnoreReason, "Slack-specific ignore reason has no contract mapping.")
        };

        return new RoutingVerdict(kind, reason);
    }
}
