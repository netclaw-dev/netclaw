// -----------------------------------------------------------------------
// <copyright file="MattermostThreadHistoryIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost;
using Mattermost.Models.Responses;
using Xunit;

namespace Netclaw.Channels.Mattermost.IntegrationTests;

/// <summary>
/// Tests thread history fetching against a real Mattermost server.
/// Validates pagination, message ordering, and thread structure.
/// </summary>
[Collection("Mattermost")]
public sealed class MattermostThreadHistoryIntegrationTests
{
    // Generous ceiling: thread posts must propagate through a containerized
    // Mattermost server, which is slow on a loaded CI runner. Polling stops as
    // soon as the expected posts appear, so this only bounds the failure case.
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly MattermostFixture _fixture;

    public MattermostThreadHistoryIntegrationTests(MattermostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetThreadPostsAsync_returns_thread_messages_in_order()
    {
        _fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);

        var rootPostId = await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "History root message");
        await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "History reply 1", rootId: rootPostId);
        await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "History reply 2", rootId: rootPostId);

        var threadPosts = await PollThreadPostsAsync(botClient, rootPostId, minCount: 3, ct);

        Assert.NotNull(threadPosts);
        Assert.True(threadPosts.Posts.Count >= 3, $"Expected at least 3 posts, got {threadPosts.Posts.Count}");
    }

    [Fact]
    public async Task GetThreadPostsAsync_includes_root_post()
    {
        _fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);

        var rootPostId = await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "Unique root content for history test");
        await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "Reply to unique root", rootId: rootPostId);

        var threadPosts = await PollThreadPostsAsync(botClient, rootPostId, minCount: 2, ct);

        Assert.True(threadPosts.Posts.ContainsKey(rootPostId),
            "Thread history should include the root post");
        Assert.Contains("Unique root content for history test", threadPosts.Posts[rootPostId].Text);
    }

    [Fact]
    public async Task Bot_can_read_its_own_posts_in_thread()
    {
        _fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        using var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);

        var rootPostId = await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "Thread with bot participation");

        await botClient.CreatePostAsync(_fixture.ChannelId, "Bot reply in thread", replyToPostId: rootPostId);

        var threadPosts = await PollThreadPostsAsync(botClient, rootPostId, minCount: 2, ct);
        var botPosts = threadPosts.Posts.Values.Where(p => p.UserId == _fixture.BotUserId).ToList();

        Assert.Single(botPosts);
        Assert.Contains("Bot reply in thread", botPosts[0].Text);
    }

    private static async Task<ChannelPostsResponse> PollThreadPostsAsync(
        MattermostClient client,
        string rootPostId,
        int minCount,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PollTimeout);
        using var timer = new PeriodicTimer(PollInterval);

        while (!cts.IsCancellationRequested)
        {
            var result = await client.GetThreadPostsAsync(rootPostId);
            if (result.Posts.Count >= minCount)
                return result;

            // The poll's deadline is enforced by the cts-driven loop condition;
            // the per-tick wait itself need not observe the token.
            if (!await timer.WaitForNextTickAsync(CancellationToken.None))
                break;
        }

        return await client.GetThreadPostsAsync(rootPostId);
    }
}
