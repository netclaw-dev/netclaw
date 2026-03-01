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
    public void ThreadReply_RehydratesSession_AfterDaemonRestart()
    {
        // ThreadTs differs from EventTs — this is a reply in an existing thread,
        // but the thread actor was lost due to a daemon restart.
        var message = CreateMessage(text: "follow up", threadTs: "1740468105.120900", isDirectMessage: false);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.StartOrContinue, decision);
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

    [Fact]
    public void FileOnlyMessage_ContinuesExistingThread()
    {
        var files = new List<SlackFileReference>
        {
            new("F1", "image.png", "image/png", 1024, "https://files.slack.com/F1/image.png")
        };
        var message = CreateMessage(text: "", threadTs: "1740468105.120900", isDirectMessage: false, files: files);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            threadExists: true,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.ContinueOnly, decision);
    }

    [Fact]
    public void FileOnlyAppMention_StartsThread()
    {
        var files = new List<SlackFileReference>
        {
            new("F1", "image.png", "image/png", 1024, "https://files.slack.com/F1/image.png")
        };
        var message = CreateAppMention(text: "", threadTs: null, files: files);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            threadExists: false,
            containsBotMention: true);

        Assert.Equal(SlackRoutingDecision.StartOrContinue, decision);
    }

    [Fact]
    public void FileShareSubtype_AllowedThrough_WhenFilesPresent()
    {
        var files = new List<SlackFileReference>
        {
            new("F1", "photo.jpg", "image/jpeg", 4096, "https://files.slack.com/F1/photo.jpg")
        };
        var message = CreateMessage(text: "", threadTs: null, isDirectMessage: false, files: files, subtype: "file_share");

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.StartOrContinue, decision);
    }

    [Fact]
    public void FileShareSubtype_WithText_AllowedThrough()
    {
        var files = new List<SlackFileReference>
        {
            new("F1", "photo.jpg", "image/jpeg", 4096, "https://files.slack.com/F1/photo.jpg")
        };
        var message = CreateMessage(text: "check this out", threadTs: null, isDirectMessage: false, files: files, subtype: "file_share");

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.StartOrContinue, decision);
    }

    [Fact]
    public void FileShareSubtype_ContinuesExistingThread()
    {
        var files = new List<SlackFileReference>
        {
            new("F1", "photo.jpg", "image/jpeg", 4096, "https://files.slack.com/F1/photo.jpg")
        };
        var message = CreateMessage(text: "", threadTs: "1740468105.120900", isDirectMessage: false, files: files, subtype: "file_share");

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            threadExists: true,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.ContinueOnly, decision);
    }

    [Fact]
    public void FileShareSubtype_Ignored_WhenNoFilesActuallyPresent()
    {
        // file_share subtype but files list is empty — treat as non-content message
        var message = CreateMessage(text: "", threadTs: null, isDirectMessage: false, files: null, subtype: "file_share");

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.Ignore, decision);
    }

    [Fact]
    public void BotMessageSubtype_StillIgnored()
    {
        var message = CreateMessage(text: "bot output", threadTs: null, isDirectMessage: false, subtype: "bot_message");

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.Ignore, decision);
    }

    [Fact]
    public void NoTextNoFiles_Ignored()
    {
        var message = CreateMessage(text: "", threadTs: null, isDirectMessage: false);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecision.Ignore, decision);
    }

    private static SlackInboundMessage CreateMessage(
        string text,
        string? threadTs,
        bool isDirectMessage,
        IReadOnlyList<SlackFileReference>? files = null,
        string? subtype = null)
    {
        return new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("C0:1"),
            ChannelId: new SlackChannelId(isDirectMessage ? "D0" : "C0"),
            ThreadTs: threadTs is not null ? new SlackThreadTs(threadTs) : null,
            EventTs: new SlackEventTs("1740468000.000001"),
            UserId: new SlackUserId("U123"),
            BotId: null,
            Text: text,
            Subtype: subtype,
            Hidden: false,
            IsDirectMessage: isDirectMessage,
            Files: files);
    }

    private static SlackInboundMessage CreateAppMention(
        string text,
        string? threadTs,
        IReadOnlyList<SlackFileReference>? files = null)
    {
        return new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: new SlackEventId("C0:2"),
            ChannelId: new SlackChannelId("C0"),
            ThreadTs: threadTs is not null ? new SlackThreadTs(threadTs) : null,
            EventTs: new SlackEventTs("1740468000.000002"),
            UserId: new SlackUserId("U123"),
            BotId: null,
            Text: text,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false,
            Files: files);
    }
}
