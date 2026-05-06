// -----------------------------------------------------------------------
// <copyright file="MattermostReplyClientIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost;
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
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        using var apiClient = _fixture.CreateBotApiClient();
        var replyClient = new MattermostNetReplyClient(botClient, apiClient);

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
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        using var apiClient = _fixture.CreateBotApiClient();
        var replyClient = new MattermostNetReplyClient(botClient, apiClient);

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
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        using var apiClient = _fixture.CreateBotApiClient();
        var replyClient = new MattermostNetReplyClient(botClient, apiClient);

        var result = await replyClient.PostReplyAsync(new MattermostPostMessage(
            ChannelId: new MattermostChannelId(_fixture.ChannelId),
            Text: "Original message text"), ct);
        Assert.NotNull(result.PostId);

        await replyClient.UpdatePostAsync(result.PostId!.Value, "Updated message text", ct);

        var updated = await botClient.GetPostAsync(result.PostId.Value.Value);
        Assert.Contains("Updated message text", updated.Text);
    }

    [Fact]
    public async Task PostNewThreadAsync_creates_top_level_post_and_returns_root_id()
    {
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
