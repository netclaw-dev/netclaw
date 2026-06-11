// -----------------------------------------------------------------------
// <copyright file="DiscordRoutingPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Discord;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Discord-specific routing policy tests. Cross-channel routing behaviors
/// (mention gating, thread continuation/rehydration, DM matrix, empty content)
/// live in <see cref="Contracts.RoutingPolicyContractTests"/> via
/// <see cref="Contracts.DiscordRoutingPolicyContractTests"/>.
/// </summary>
public class DiscordRoutingPolicyTests
{
    [Fact]
    public void ThreadReply_StartsSession_WhenMentioned()
    {
        var message = new DiscordGatewayMessage(
            EventId: new DiscordEventId("ev-1"),
            ChannelId: new DiscordChannelId("ch-1"),
            ReplyChannelId: new DiscordReplyChannelId("thread-ch-1"),
            MessageId: new DiscordMessageId("m-1"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-ch-1"),
            RootMessageId: null,
            SenderId: new DiscordUserId("u-1"),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: true,
            Text: "<@123> follow up",
            ReceivedAt: TimeProvider.System.GetUtcNow(),
            IsInThread: true);

        var decision = DiscordRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: true);

        Assert.Equal(DiscordRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }
}
