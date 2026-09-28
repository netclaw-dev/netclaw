// -----------------------------------------------------------------------
// <copyright file="TelegramRoutingPolicyContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Telegram;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Telegram enrolls in the universal routing contract only. Its topics key
/// sessions (<c>{chatId}/{topicId}</c>) but are not addressing signals, and
/// DM gating lives in the ACL before routing, so the thread-reply and
/// policy-level-DM-gating layers do not apply. Telegram's own continuation
/// signal is a reply to the bot's message, covered by
/// <c>TelegramRoutingPolicyTests</c>.
/// </summary>
public sealed class TelegramRoutingPolicyContractTests : RoutingPolicyContractTests
{
    protected override RoutingVerdict EvaluateRouting(
        bool mentionOnly, bool isDm, bool containsMention, string text)
    {
        var message = new TelegramInboundMessage(
            ChatId: TelegramContractIds.LongId(isDm ? "dm-channel" : "ch-1"),
            UserId: TelegramContractIds.LongId("user-1"),
            MessageId: TelegramContractIds.IntId("evt-1"),
            Text: text,
            IsDirectMessage: isDm,
            ContainsBotMention: containsMention);

        var decision = TelegramRoutingPolicy.Evaluate(message, mentionOnly);

        var kind = decision.Kind switch
        {
            TelegramRoutingDecisionKind.StartOrContinue => RoutingVerdictKind.Route,
            TelegramRoutingDecisionKind.Ignore => RoutingVerdictKind.Ignore,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision), decision.Kind, "Unmapped Telegram routing decision kind.")
        };

        // GroupMentionRequired is Telegram's group-post "needs a mention"
        // reason — the same semantic as the contract's ChannelMentionRequired.
        var reason = decision.IgnoreReason switch
        {
            null => (RoutingIgnoreReason?)null,
            TelegramRoutingIgnoreReason.NoContent => RoutingIgnoreReason.NoContent,
            TelegramRoutingIgnoreReason.GroupMentionRequired => RoutingIgnoreReason.ChannelMentionRequired,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision), decision.IgnoreReason, "Telegram-specific ignore reason has no contract mapping.")
        };

        return new RoutingVerdict(kind, reason);
    }
}
