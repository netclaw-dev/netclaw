// -----------------------------------------------------------------------
// <copyright file="MattermostReplyClientIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost;
using Mattermost.Models.Posts;
using Netclaw.Channels.Mattermost.Transport;
using Xunit;

namespace Netclaw.Channels.Mattermost.IntegrationTests;

/// <summary>
/// Tests the reply client and outbound client against a real Mattermost server.
/// Validates message posting, thread replies, and DM channel creation.
/// </summary>
[Collection("Mattermost")]
public sealed class MattermostReplyClientIntegrationTests
{
    private readonly MattermostFixture _fixture;

    public MattermostReplyClientIntegrationTests(MattermostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PostReplyAsync_creates_top_level_post()
    {
        _fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        var replyClient = new MattermostNetReplyClient(botClient);

        var result = await replyClient.PostReplyAsync(new MattermostPostMessage(
            ChannelId: new MattermostChannelId(_fixture.ChannelId),
            Text: "Top-level post from reply client test"), ct);

        Assert.NotNull(result.PostId);
        Assert.False(string.IsNullOrEmpty(result.PostId!.Value.Value));

        var post = await botClient.GetPostAsync(result.PostId.Value.Value);
        Assert.Contains("Top-level post from reply client test", post.Text);
    }

    [Fact]
    public async Task PostReplyAsync_creates_thread_reply()
    {
        _fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        var replyClient = new MattermostNetReplyClient(botClient);

        var root = await replyClient.PostReplyAsync(new MattermostPostMessage(
            ChannelId: new MattermostChannelId(_fixture.ChannelId),
            Text: "Thread root for reply test"), ct);
        Assert.NotNull(root.PostId);

        var reply = await replyClient.PostReplyAsync(new MattermostPostMessage(
            ChannelId: new MattermostChannelId(_fixture.ChannelId),
            Text: "Thread reply from reply client test",
            RootPostId: root.PostId), ct);
        Assert.NotNull(reply.PostId);

        var replyPost = await botClient.GetPostAsync(reply.PostId!.Value.Value);
        Assert.Equal(root.PostId!.Value.Value, replyPost.RootId);
    }

    [Fact]
    public async Task UpdatePostAsync_modifies_message_text()
    {
        _fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        var replyClient = new MattermostNetReplyClient(botClient);

        var result = await replyClient.PostReplyAsync(new MattermostPostMessage(
            ChannelId: new MattermostChannelId(_fixture.ChannelId),
            Text: "Original message text"), ct);
        Assert.NotNull(result.PostId);

        await replyClient.UpdatePostAsync(result.PostId!.Value, "Updated message text", attachments: null, ct);

        var updated = await botClient.GetPostAsync(result.PostId.Value.Value);
        Assert.Contains("Updated message text", updated.Text);
    }

    [Fact]
    public async Task PostReplyAsync_round_trips_attachment_with_button_action()
    {
        _fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        var replyClient = new MattermostNetReplyClient(botClient);

        var attachment = new MattermostAttachment(
            Fallback: "Approve or deny — reply with A or B",
            Color: "#3AA3E3",
            Text: "Tool approval required",
            Actions:
            [
                new MattermostAttachmentAction(
                    Id: "tool_approval_approve_once",
                    Name: "Approve once",
                    IntegrationUrl: "https://example.invalid/callback",
                    Context: new Dictionary<string, string> { ["action_token"] = "abc123" },
                    Style: "primary"),
                new MattermostAttachmentAction(
                    Id: "tool_approval_deny",
                    Name: "Deny",
                    IntegrationUrl: "https://example.invalid/callback",
                    Context: new Dictionary<string, string> { ["action_token"] = "def456" },
                    Style: "danger")
            ]);

        var result = await replyClient.PostReplyAsync(new MattermostPostMessage(
            ChannelId: new MattermostChannelId(_fixture.ChannelId),
            Text: "Post with attachment + buttons",
            Attachments: [attachment]), ct);
        Assert.NotNull(result.PostId);

        // Re-fetch via the SDK and assert the server echoes the attachment
        // shape we sent, including the typed Type = Button payload that 5.0's
        // PostPropsButtonAction produces. This is the round-trip that the
        // 4.x-era HTTP-bypass shim used to validate by hand.
        //
        // Note: Mattermost server intentionally strips integration.url from
        // posts on read-back — that URL is the bot's private callback endpoint
        // and is server-side only. We assert on Id/Name/Type/Style which are
        // what 5.0's typed model actually changed.
        var post = await botClient.GetPostAsync(result.PostId!.Value.Value);
        var serverAttachment = Assert.Single(post.Props.Attachments);
        Assert.Equal(2, serverAttachment.Actions.Count);
        Assert.All(serverAttachment.Actions, action =>
            Assert.Equal(PostActionType.Button, action.Type));
        Assert.Equal("tool_approval_approve_once", serverAttachment.Actions[0].Id);
        Assert.Equal("Approve once", serverAttachment.Actions[0].Name);
        Assert.Equal(ActionStyle.Primary, serverAttachment.Actions[0].Style);
        Assert.Equal("tool_approval_deny", serverAttachment.Actions[1].Id);
        Assert.Equal("Deny", serverAttachment.Actions[1].Name);
        Assert.Equal(ActionStyle.Danger, serverAttachment.Actions[1].Style);
    }

    [Fact]
    public async Task PostNewThreadAsync_creates_top_level_post_and_returns_root_id()
    {
        _fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        var outboundClient = new MattermostNetOutboundClient(botClient);

        var result = await outboundClient.PostNewThreadAsync(
            new MattermostChannelId(_fixture.ChannelId),
            "Outbound client new thread test", ct);

        Assert.Equal(_fixture.ChannelId, result.ChannelId.Value);
        Assert.False(string.IsNullOrEmpty(result.RootPostId.Value));

        var post = await botClient.GetPostAsync(result.RootPostId.Value);
        Assert.Contains("Outbound client new thread test", post.Text);
    }

    [Fact]
    public async Task OpenDmChannelAsync_creates_dm_channel_with_test_user()
    {
        _fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        // Initialize CurrentUserInfo — in production this is done by the shared gateway client
        await botClient.GetMeAsync();
        var outboundClient = new MattermostNetOutboundClient(botClient);

        var dmChannelId = await outboundClient.OpenDmChannelAsync(
            new MattermostUserId(_fixture.TestUserId), ct);

        Assert.False(string.IsNullOrEmpty(dmChannelId.Value));

        var post = await botClient.CreatePostAsync(
            channelId: dmChannelId.Value,
            message: "DM from bot in integration test");
        Assert.Equal(dmChannelId.Value, post.ChannelId);
    }
}
