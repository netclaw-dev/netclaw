// -----------------------------------------------------------------------
// <copyright file="MattermostRoutingPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public class MattermostRoutingPolicyTests
{
    [Fact]
    public void MessageWithoutMention_DoesNotStartThread_WhenMentionOnly()
    {
        var message = CreateMessage(text: "hello", rootPostId: "rootpost123456789012345678");

        var decision = MattermostRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(MattermostRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    [Fact]
    public void TopLevelMessage_WithoutMention_Ignored_WhenMentionOnly()
    {
        var message = CreateMessage(text: "hello");

        var decision = MattermostRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(MattermostRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(MattermostRoutingIgnoreReason.ChannelMentionRequired, decision.IgnoreReason);
    }

    [Fact]
    public void MessageWithMention_StartsThread_WhenMentionOnly()
    {
        var message = CreateMessage(text: "@bot hello");

        var decision = MattermostRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: true);

        Assert.Equal(MattermostRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    [Fact]
    public void ExistingThread_ContinuesWithoutMention()
    {
        var message = CreateMessage(text: "follow up", rootPostId: "rootpost123456789012345678");

        var decision = MattermostRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: true,
            containsBotMention: false);

        Assert.Equal(MattermostRoutingDecisionKind.ContinueOnly, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    [Fact]
    public void ThreadReply_RehydratesSession_WhenNoExistingActor()
    {
        var message = CreateMessage(text: "follow up", rootPostId: "rootpost123456789012345678");

        var decision = MattermostRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(MattermostRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    [Theory]
    [InlineData(true, false, false, MattermostRoutingDecisionKind.StartOrContinue, null)]
    [InlineData(false, false, false, MattermostRoutingDecisionKind.Ignore, MattermostRoutingIgnoreReason.DmNotAllowed)]
    [InlineData(true, true, false, MattermostRoutingDecisionKind.Ignore, MattermostRoutingIgnoreReason.DmMentionRequired)]
    [InlineData(true, true, true, MattermostRoutingDecisionKind.StartOrContinue, null)]
    internal void DirectMessage_routing_decision(
        bool allowDirectMessages,
        bool mentionRequiredInDm,
        bool containsBotMention,
        MattermostRoutingDecisionKind expectedKind,
        MattermostRoutingIgnoreReason? expectedReason)
    {
        var message = CreateMessage(text: "hey", isDirectMessage: true);

        var decision = MattermostRoutingPolicy.Evaluate(
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

        var decision = MattermostRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: false,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(MattermostRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(MattermostRoutingIgnoreReason.NoContent, decision.IgnoreReason);
    }

    [Fact]
    public void MentionOnly_false_StartsWithoutMention()
    {
        var message = CreateMessage(text: "hello");

        var decision = MattermostRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(MattermostRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    private static MattermostGatewayMessage CreateMessage(
        string text,
        string? rootPostId = null,
        bool isDirectMessage = false)
    {
        return new MattermostGatewayMessage(
            EventId: new MattermostEventId("ev-1"),
            ChannelId: new MattermostChannelId(isDirectMessage ? "dm-ch-1" : "ch-1"),
            PostId: new MattermostPostId("post-1"),
            RootPostId: rootPostId is not null
                ? new MattermostRootPostId(rootPostId)
                : new MattermostRootPostId(string.Empty),
            SenderId: new MattermostUserId("u-1"),
            IsBotMessage: false,
            IsDirectMessage: isDirectMessage,
            ContainsBotMention: false,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());
    }
}
