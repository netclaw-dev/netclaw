// -----------------------------------------------------------------------
// <copyright file="MattermostChannelResilienceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Mattermost;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostChannelResilienceTests : TestKit
{
    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    [Fact]
    public async Task Live_disconnect_triggers_bounded_reconnect_loop()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-24T18:30:00Z"));
        var gatewayClient = new FakeMattermostGatewayClient();
        var channel = new MattermostChannel(
            Sys,
            new FailingSessionPipeline(new InvalidOperationException("not used")),
            ingressGate: null!,
            gatewayClient,
            new RecordingMattermostReplyClient(),
            new NullContentScanner(),
            SafePromptInjectionDetector.Instance,
            new FakeHttpClientFactory(),
            threadHistoryFetcher: null,
            NullNotificationSink.Instance,
            time,
            new MattermostChannelOptions
            {
                Enabled = true,
                ServerUrl = "https://mattermost.example.com",
                BotToken = new SensitiveString("token")
            },
            NullLogger<MattermostChannel>.Instance,
            new ToolConfig(),
            TestMattermostGatewayDeps.DefaultVisionCapableModel,
            TestMattermostGatewayDeps.NewTestPaths());

        await channel.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, gatewayClient.ConnectCallCount);

        await gatewayClient.FireDisconnectedAsync(new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            "socket dropped"));

        await AwaitAssertAsync(() =>
        {
            var health = channel.GetHealthAsync(TestContext.Current.CancellationToken).Result;
            Assert.Equal(ChannelHealthStatus.Disconnected, health.Status);
        }, cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 10 && !gatewayClient.SecondConnectObserved.Task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        await gatewayClient.SecondConnectObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, gatewayClient.ConnectCallCount);
        Assert.True(gatewayClient.DisconnectCallCount >= 1);

        await channel.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FakeMattermostGatewayClient : IMattermostGatewayClient
    {
        public event Func<MattermostGatewayMessage, Task>? MessageReceived
        {
            add { }
            remove { }
        }

        public event Func<ChannelConnectException, Task>? Disconnected;

        public bool IsConnected { get; private set; }
        public MattermostUserId? BotUserId { get; private set; } = new("bot-user");
        public string? BotUsername { get; private set; } = "netclaw-bot";
        public int ConnectCallCount { get; private set; }
        public int DisconnectCallCount { get; private set; }
        public TaskCompletionSource<int> SecondConnectObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            IsConnected = true;
            if (ConnectCallCount >= 2)
                SecondConnectObserved.TrySetResult(ConnectCallCount);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCallCount++;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public async Task FireDisconnectedAsync(ChannelConnectException failure)
        {
            IsConnected = false;
            if (Disconnected is { } disconnected)
                await disconnected(failure);
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
