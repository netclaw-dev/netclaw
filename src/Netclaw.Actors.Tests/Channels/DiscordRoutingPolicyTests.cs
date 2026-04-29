// -----------------------------------------------------------------------
// <copyright file="DiscordRoutingPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Discord;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public class DiscordRoutingPolicyTests
{
    [Fact]
    public void MessageWithoutMention_DoesNotStartThread_WhenMentionOnly()
    {
        var message = CreateMessage(text: "hello", rootMessageId: "m-1");

        var decision = DiscordRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(DiscordRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(DiscordRoutingIgnoreReason.ChannelMentionRequired, decision.IgnoreReason);
    }

    [Fact]
    public void MessageWithMention_StartsThread_WhenMentionOnly()
    {
        var message = CreateMessage(text: "<@123> hello", rootMessageId: "m-1");

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

    [Fact]
    public void ExistingThread_ContinuesWithoutMention()
    {
        var message = CreateMessage(text: "follow up", rootMessageId: null, isThread: true);

        var decision = DiscordRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: true,
            containsBotMention: false);

        Assert.Equal(DiscordRoutingDecisionKind.ContinueOnly, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    [Fact]
    public void ThreadReply_RehydratesSession_WhenNoExistingActor()
    {
        var message = CreateMessage(text: "follow up", rootMessageId: null, isThread: true);

        var decision = DiscordRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(DiscordRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    [Fact]
    public void ThreadReply_StartsSession_WhenMentioned()
    {
        var message = CreateMessage(text: "<@123> follow up", rootMessageId: null, isThread: true);

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

    [Theory]
    [InlineData(true, false, false, DiscordRoutingDecisionKind.StartOrContinue, null)]
    [InlineData(false, false, false, DiscordRoutingDecisionKind.Ignore, DiscordRoutingIgnoreReason.DmNotAllowed)]
    [InlineData(true, true, false, DiscordRoutingDecisionKind.Ignore, DiscordRoutingIgnoreReason.DmMentionRequired)]
    [InlineData(true, true, true, DiscordRoutingDecisionKind.StartOrContinue, null)]
    internal void DirectMessage_routing_decision(
        bool allowDirectMessages,
        bool mentionRequiredInDm,
        bool containsBotMention,
        DiscordRoutingDecisionKind expectedKind,
        DiscordRoutingIgnoreReason? expectedReason)
    {
        var message = CreateMessage(text: "hey", isDirectMessage: true);

        var decision = DiscordRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: allowDirectMessages,
            mentionRequiredInDm: mentionRequiredInDm,
            threadExists: false,
            containsBotMention: containsBotMention);

        Assert.Equal(expectedKind, decision.Kind);
        Assert.Equal(expectedReason, decision.IgnoreReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    internal void EmptyContent_Ignored(string text)
    {
        var message = CreateMessage(text: text);

        var decision = DiscordRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: false,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(DiscordRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(DiscordRoutingIgnoreReason.NoContent, decision.IgnoreReason);
    }

    [Fact]
    public void MentionOnly_false_StartsWithoutMention()
    {
        var message = CreateMessage(text: "hello", rootMessageId: "m-1");

        var decision = DiscordRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(DiscordRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    private static DiscordGatewayMessage CreateMessage(
        string text,
        string? rootMessageId = null,
        bool isDirectMessage = false,
        bool isThread = false)
    {
        return new DiscordGatewayMessage(
            EventId: new DiscordEventId("ev-1"),
            ChannelId: new DiscordChannelId(isDirectMessage ? "dm-1" : "ch-1"),
            ReplyChannelId: new DiscordReplyChannelId(isThread ? "thread-ch-1" : "ch-1"),
            MessageId: new DiscordMessageId("m-1"),
            ThreadOrMessageId: new DiscordThreadOrMessageId(isThread ? "thread-ch-1" : "m-1"),
            RootMessageId: rootMessageId is not null ? new DiscordMessageId(rootMessageId) : null,
            SenderId: new DiscordUserId("u-1"),
            IsBotMessage: false,
            IsDirectMessage: isDirectMessage,
            ContainsBotMention: false,
            Text: text,
            ReceivedAt: DateTimeOffset.UtcNow,
            IsInThread: isThread);
    }
}
