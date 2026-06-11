// -----------------------------------------------------------------------
// <copyright file="MattermostGatewayIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Mattermost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels.Mattermost.Transport;
using Xunit;

namespace Netclaw.Channels.Mattermost.IntegrationTests;

/// <summary>
/// Tests the gateway client against a real Mattermost server.
/// Validates WebSocket event delivery, message normalization, and connection lifecycle.
/// </summary>
[Collection("Mattermost")]
public sealed class MattermostGatewayIntegrationTests(
    MattermostFixture fixture,
    ITestOutputHelper output) : TestKit(output: output)
{
    private readonly MattermostFixture _fixture = fixture;

    // Generous ceiling for a real WebSocket round-trip through a containerized
    // Mattermost server — a loaded CI runner is far slower than a dev machine.
    // The TaskCompletionSource resolves as soon as the event arrives, so this
    // only bounds the failure case; it does not slow the happy path.
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(60);

    protected override Config? Config =>
        ConfigurationFactory.ParseString("akka.test.default-timeout = 30s");

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task BotUserId_is_resolved_after_connect()
    {
        var gateway = await ConnectGatewayAsync();
        try
        {
            Assert.NotNull(gateway.BotUserId);
            Assert.Equal(_fixture.BotUserId, gateway.BotUserId!.Value.Value);
        }
        finally
        {
            await DisconnectGatewayAsync(gateway);
        }
    }

    [Fact]
    public async Task Receives_message_posted_by_test_user()
    {
        var gateway = await ConnectGatewayAsync();
        var ct = TestContext.Current.CancellationToken;
        var receivedTcs = new TaskCompletionSource<MattermostGatewayMessage>();

        gateway.MessageReceived += msg =>
        {
            receivedTcs.TrySetResult(msg);
            return Task.CompletedTask;
        };

        try
        {
            await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "Hello from integration test");

            var received = await receivedTcs.Task.WaitAsync(EventTimeout, ct);

            Assert.Equal(_fixture.ChannelId, received.ChannelId.Value);
            Assert.Contains("Hello from integration test", received.Text);
            Assert.False(received.IsBotMessage);
            Assert.False(received.IsDirectMessage);
        }
        finally
        {
            await DisconnectGatewayAsync(gateway);
        }
    }

    [Fact]
    public async Task Thread_reply_has_root_post_id()
    {
        var gateway = await ConnectGatewayAsync();
        var ct = TestContext.Current.CancellationToken;
        var replyTcs = new TaskCompletionSource<MattermostGatewayMessage>();

        gateway.MessageReceived += msg =>
        {
            if (!msg.RootPostId.IsEmpty)
                replyTcs.TrySetResult(msg);
            return Task.CompletedTask;
        };

        try
        {
            var rootPostId = await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "Thread root message");

            await _fixture.PostAsTestUserAsync(_fixture.ChannelId, "Thread reply message", rootId: rootPostId);

            var reply = await replyTcs.Task.WaitAsync(EventTimeout, ct);

            Assert.Equal(rootPostId, reply.RootPostId.Value);
            Assert.Contains("Thread reply message", reply.Text);
        }
        finally
        {
            await DisconnectGatewayAsync(gateway);
        }
    }

    private async Task<MattermostNetGatewayClient> ConnectGatewayAsync()
    {
        _fixture.SkipIfUnavailable();
        var botClient = new MattermostClient(_fixture.ServerUrl, _fixture.BotToken);
        var gateway = new MattermostNetGatewayClient(
            Sys,
            botClient,
            TimeProvider.System,
            NullLogger<MattermostNetGatewayClient>.Instance);

        try
        {
            await gateway.ConnectAsync(_fixture.ServerUrl, _fixture.BotToken,
                TestContext.Current.CancellationToken);
            return gateway;
        }
        catch
        {
            gateway.Dispose();
            throw;
        }
    }

    private static async Task DisconnectGatewayAsync(MattermostNetGatewayClient gateway)
    {
        await gateway.DisconnectAsync();
        gateway.Dispose();
    }
}
