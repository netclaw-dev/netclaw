using Netclaw.Channels.Slack;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public class SlackRoutingPolicyTests
{
    [Fact]
    public void MessageWithoutMention_DoesNotStartThread_WhenMentionOnly()
    {
        var message = CreateMessage(text: "hello", threadTs: null, isDirectMessage: false);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.Ignore, decision);
    }

    [Fact]
    public void AppMention_StartsThread_WhenMentionOnly()
    {
        var message = CreateAppMention(text: "<@U1> hello", threadTs: null);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            threadExists: false,
            containsBotMention: true);

        Assert.Equal(SlackRoutingDecision.StartOrContinue, decision);
    }

    [Fact]
    public void ExistingThreadReply_ContinuesWithoutMention()
    {
        var message = CreateMessage(text: "follow up", threadTs: "1740468105.120900", isDirectMessage: false);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            threadExists: true,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.ContinueOnly, decision);
    }

    [Fact]
    public void DirectMessage_ProcessesWithoutMention_WhenEnabled()
    {
        var message = CreateMessage(text: "hey", threadTs: null, isDirectMessage: true);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.StartOrContinue, decision);
    }

    [Fact]
    public void DirectMessage_Ignored_WhenDisabled()
    {
        var message = CreateMessage(text: "hey", threadTs: null, isDirectMessage: true);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.Ignore, decision);
    }

    private static SlackInboundMessage CreateMessage(string text, string? threadTs, bool isDirectMessage)
    {
        return new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: "C0:1",
            ChannelId: isDirectMessage ? "D0" : "C0",
            ThreadTs: threadTs,
            EventTs: "1740468000.000001",
            UserId: "U123",
            BotId: null,
            Text: text,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: isDirectMessage);
    }

    private static SlackInboundMessage CreateAppMention(string text, string? threadTs)
    {
        return new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: "C0:2",
            ChannelId: "C0",
            ThreadTs: threadTs,
            EventTs: "1740468000.000002",
            UserId: "U123",
            BotId: null,
            Text: text,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false);
    }
}
