// -----------------------------------------------------------------------
// <copyright file="SlackRoutingPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(SlackRoutingIgnoreReason.ChannelMentionRequired, decision.IgnoreReason);
    }

    [Fact]
    public void AppMention_StartsThread_WhenMentionOnly()
    {
        var message = CreateAppMention(text: "<@U1> hello", threadTs: null);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: true);

        Assert.Equal(SlackRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    [Fact]
    public void ExistingThreadReply_ContinuesWithoutMention()
    {
        var message = CreateMessage(text: "follow up", threadTs: "1740468105.120900", isDirectMessage: false);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: true,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.ContinueOnly, decision.Kind);
        Assert.Null(decision.IgnoreReason);
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
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    [Theory]
    [InlineData(true, false, false, SlackRoutingDecisionKind.StartOrContinue, null)]
    [InlineData(false, false, false, SlackRoutingDecisionKind.Ignore, SlackRoutingIgnoreReason.DmNotAllowed)]
    [InlineData(true, true, false, SlackRoutingDecisionKind.Ignore, SlackRoutingIgnoreReason.DmMentionRequired)]
    [InlineData(true, true, true, SlackRoutingDecisionKind.StartOrContinue, null)]
    internal void DirectMessage_routing_decision(
        bool allowDirectMessages,
        bool mentionRequiredInDm,
        bool containsBotMention,
        SlackRoutingDecisionKind expectedKind,
        SlackRoutingIgnoreReason? expectedReason)
    {
        var message = CreateMessage(text: "hey", threadTs: null, isDirectMessage: true);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: allowDirectMessages,
            mentionRequiredInDm: mentionRequiredInDm,
            threadExists: false,
            containsBotMention: containsBotMention);

        Assert.Equal(expectedKind, decision.Kind);
        Assert.Equal(expectedReason, decision.IgnoreReason);
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
            mentionRequiredInDm: false,
            threadExists: true,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.ContinueOnly, decision.Kind);
        Assert.Null(decision.IgnoreReason);
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
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: true);

        Assert.Equal(SlackRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
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
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
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
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
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
            mentionRequiredInDm: false,
            threadExists: true,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.ContinueOnly, decision.Kind);
        Assert.Null(decision.IgnoreReason);
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
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        // hasContent=false wins before the subtype check, so this is NoContent
        Assert.Equal(SlackRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(SlackRoutingIgnoreReason.NoContent, decision.IgnoreReason);
    }

    [Fact]
    public void BotMessageSubtype_StillIgnored()
    {
        var message = CreateMessage(text: "bot output", threadTs: null, isDirectMessage: false, subtype: "bot_message");

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: false,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(SlackRoutingIgnoreReason.UnsupportedSubtype, decision.IgnoreReason);
    }

    [Fact]
    public void NoTextNoFiles_Ignored()
    {
        var message = CreateMessage(text: "", threadTs: null, isDirectMessage: false);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: false,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(SlackRoutingIgnoreReason.NoContent, decision.IgnoreReason);
    }

    [Fact]
    public void HiddenMessage_IsIgnored()
    {
        var message = CreateMessage(text: "hello", threadTs: null, isDirectMessage: true, hidden: true);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(SlackRoutingIgnoreReason.HiddenMessage, decision.IgnoreReason);
    }

    [Fact]
    public void DirectMessage_WithFileShareSubtype_IsRouted()
    {
        // Regression coverage for the 13:38 Gemma image incident class:
        // a DM carrying a file_share subtype with an attached image must
        // reach StartOrContinue without a mention.
        var files = new List<SlackFileReference>
        {
            new("F1", "image.png", "image/png", 160_591, "https://files.slack.com/F1/image.png")
        };
        var message = CreateMessage(
            text: "What does this image show?",
            threadTs: null,
            isDirectMessage: true,
            files: files,
            subtype: "file_share");

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    [Fact]
    public void DirectMessage_WithFilesNoSubtype_IsRouted()
    {
        // Modern Slack file uploads may deliver a plain message event with
        // files populated and no subtype at all. Must also route.
        var files = new List<SlackFileReference>
        {
            new("F1", "image.png", "image/png", 1024, "https://files.slack.com/F1/image.png")
        };
        var message = CreateMessage(
            text: "",
            threadTs: null,
            isDirectMessage: true,
            files: files);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.StartOrContinue, decision.Kind);
        Assert.Null(decision.IgnoreReason);
    }

    [Fact]
    public void DirectMessage_HiddenFileShare_IsIgnoredAsHidden()
    {
        // Slack's message_changed / edited delivery can re-deliver a file_share
        // message with hidden=true. Must be dropped as HiddenMessage, not as
        // UnsupportedSubtype — the policy order matters here.
        var files = new List<SlackFileReference>
        {
            new("F1", "image.png", "image/png", 1024, "https://files.slack.com/F1/image.png")
        };
        var message = CreateMessage(
            text: "What does this image show?",
            threadTs: null,
            isDirectMessage: true,
            files: files,
            subtype: "file_share",
            hidden: true);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: true,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(SlackRoutingIgnoreReason.HiddenMessage, decision.IgnoreReason);
    }

    [Fact]
    public void BlockActionKind_IsIgnoredAsWrongKind()
    {
        // BlockAction events are routed through a different code path; the
        // inbound message routing policy must defensively refuse them.
        var message = new SlackInboundMessage(
            Kind: SlackInboundKind.BlockAction,
            EventId: new SlackEventId("C0:1"),
            ChannelId: new SlackChannelId("C0"),
            ThreadTs: null,
            EventTs: new SlackEventTs("1740468000.000001"),
            UserId: new SlackUserId("U123"),
            BotId: null,
            Text: "hello",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false,
            Files: null);

        var decision = SlackRoutingPolicy.Evaluate(
            message,
            mentionOnly: false,
            allowDirectMessages: true,
            mentionRequiredInDm: false,
            threadExists: false,
            containsBotMention: false);

        Assert.Equal(SlackRoutingDecisionKind.Ignore, decision.Kind);
        Assert.Equal(SlackRoutingIgnoreReason.WrongKind, decision.IgnoreReason);
    }

    private static SlackInboundMessage CreateMessage(
        string text,
        string? threadTs,
        bool isDirectMessage,
        IReadOnlyList<SlackFileReference>? files = null,
        string? subtype = null,
        bool hidden = false)
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
            Hidden: hidden,
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
