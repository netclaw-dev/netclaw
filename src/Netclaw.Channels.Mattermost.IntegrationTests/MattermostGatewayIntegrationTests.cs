// -----------------------------------------------------------------------
// <copyright file="MattermostGatewayIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels.Mattermost.Transport;
using Xunit;

namespace Netclaw.Channels.Mattermost.IntegrationTests;

/// <summary>
/// Tests the gateway client against a real Mattermost server.
/// Validates WebSocket event delivery, message normalization, and connection lifecycle.
/// </summary>
[Collection("Mattermost")]
public sealed class MattermostGatewayIntegrationTests : IAsyncLifetime
{
    private readonly MattermostFixture _fixture;
    private MattermostClient? _botClient;
    private MattermostNetGatewayClient? _gateway;

    // Generous ceiling for a real WebSocket round-trip through a containerized
    // Mattermost server — a loaded CI runner is far slower than a dev machine.
    // The TaskCompletionSource resolves as soon as the event arrives, so this
    // only bounds the failure case; it does not slow the happy path.
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(60);

    public MattermostGatewayIntegrationTests(MattermostFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _fixture.SkipIfUnavailable();
        _botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        _gateway = new MattermostNetGatewayClient(
            _botClient,
            TimeProvider.System,
            NullLogger<MattermostNetGatewayClient>.Instance);

        await _gateway.ConnectAsync(_fixture.ServerUrl, _fixture.BotToken,
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null)
        {
            await _gateway.DisconnectAsync();
            _gateway.Dispose();
        }
    }

    [Fact]
    public void BotUserId_is_resolved_after_connect()
    {
        Assert.NotNull(_gateway!.BotUserId);
        Assert.Equal(_fixture.BotUserId, _gateway.BotUserId!.Value.Value);
    }

    [Fact]
    public async Task Receives_message_posted_by_test_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var receivedTcs = new TaskCompletionSource<MattermostGatewayMessage>();

        _gateway!.MessageReceived += msg =>
        {
            receivedTcs.TrySetResult(msg);
            return Task.CompletedTask;
        };

        await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "Hello from integration test");

        var received = await receivedTcs.Task.WaitAsync(EventTimeout, ct);

        Assert.Equal(_fixture.ChannelId, received.ChannelId.Value);
        Assert.Contains("Hello from integration test", received.Text);
        Assert.False(received.IsBotMessage);
        Assert.False(received.IsDirectMessage);
    }

    [Fact]
    public async Task Thread_reply_has_root_post_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var replyTcs = new TaskCompletionSource<MattermostGatewayMessage>();

        _gateway!.MessageReceived += msg =>
        {
            if (!msg.RootPostId.IsEmpty)
                replyTcs.TrySetResult(msg);
            return Task.CompletedTask;
        };

        var rootPostId = await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "Thread root message");

        await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "Thread reply message", rootId: rootPostId);

        var reply = await replyTcs.Task.WaitAsync(EventTimeout, ct);

        Assert.Equal(rootPostId, reply.RootPostId.Value);
        Assert.Contains("Thread reply message", reply.Text);
    }
}
