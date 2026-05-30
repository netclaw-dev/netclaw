// -----------------------------------------------------------------------
// <copyright file="DiscordChannelHealthTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordChannelHealthTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override Config? Config =>
        ConfigurationFactory.ParseString("akka.test.default-timeout = 5s");

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Reports_healthy_only_when_gateway_is_ready()
    {
        var gateway = new FakeDiscordGatewayClient
        {
            IsConnected = true,
            IsReady = true,
            BotUserId = new DiscordUserId("bot-1")
        };
        var channel = CreateChannel(gateway);

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelHealthStatus.Healthy, health.Status);
        Assert.Null(health.Detail);
    }

    [Fact]
    public async Task Reports_degraded_when_gateway_is_connected_but_not_ready()
    {
        var gateway = new FakeDiscordGatewayClient
        {
            IsConnected = true,
            IsReady = false,
            HealthDetail = "Discord.Net resumed a stale gateway session."
        };
        var channel = CreateChannel(gateway);

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelHealthStatus.Degraded, health.Status);
        Assert.Equal("Discord.Net resumed a stale gateway session.", health.Detail);
    }

    [Fact]
    public async Task Clean_reconnect_request_runs_clean_disconnect_then_connect()
    {
        var gateway = new FakeDiscordGatewayClient
        {
            IsConnected = true,
            IsReady = false,
            HealthDetail = "Discord.Net resumed a stale gateway session.",
            BotUserId = new DiscordUserId("bot-1")
        };
        var channel = CreateChannel(gateway);

        try
        {
            await gateway.RaiseCleanReconnectRequiredAsync("Discord.Net resumed a stale gateway session.");

            await AwaitAssertAsync(() =>
            {
                Assert.Equal(1, gateway.DisconnectCount);
                Assert.Equal(1, gateway.ConnectCount);
                Assert.True(gateway.IsReady);
            }, duration: TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            await channel.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Clean_reconnect_retries_when_connect_returns_not_ready()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-30T00:00:00Z"));
        var gateway = new FakeDiscordGatewayClient
        {
            IsConnected = true,
            IsReady = false,
            HealthDetail = "Discord.Net resumed a stale gateway session.",
            BotUserId = new DiscordUserId("bot-1")
        };
        gateway.ConnectReadyResults.Enqueue(false);
        gateway.ConnectReadyResults.Enqueue(true);
        var channel = CreateChannel(gateway, timeProvider);

        try
        {
            await gateway.RaiseCleanReconnectRequiredAsync("Discord.Net resumed a stale gateway session.");

            await AwaitAssertAsync(() =>
            {
                Assert.Equal(1, gateway.ConnectCount);
                Assert.False(gateway.IsReady);
            }, duration: TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

            await AwaitAssertAsync(() =>
            {
                if (gateway.ConnectCount < 2)
                    timeProvider.Advance(TimeSpan.FromSeconds(5));

                Assert.Equal(2, gateway.DisconnectCount);
                Assert.Equal(2, gateway.ConnectCount);
                Assert.True(gateway.IsReady);
            }, duration: TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            await channel.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Clean_reconnect_request_during_active_reconnect_is_not_dropped()
    {
        var gateway = new FakeDiscordGatewayClient
        {
            IsConnected = true,
            IsReady = false,
            HealthDetail = "Discord.Net resumed a stale gateway session.",
            BotUserId = new DiscordUserId("bot-1"),
            RaiseCleanReconnectDuringFirstConnect = true
        };
        var channel = CreateChannel(gateway);

        try
        {
            await gateway.RaiseCleanReconnectRequiredAsync("Discord.Net resumed a stale gateway session.");

            await AwaitAssertAsync(() =>
            {
                Assert.Equal(2, gateway.DisconnectCount);
                Assert.Equal(2, gateway.ConnectCount);
                Assert.True(gateway.IsReady);
            }, duration: TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            await channel.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private DiscordChannel CreateChannel(
        FakeDiscordGatewayClient gatewayClient,
        TimeProvider? timeProvider = null)
    {
        return new DiscordChannel(
            Sys,
            pipeline: null!,
            new SessionIngressGate(),
            gatewayClient,
            new UnconfiguredDiscordReplyClient(),
            new NullContentScanner(),
            SafePromptInjectionDetector.Instance,
            new FakeHttpClientFactory(),
            threadHistoryFetcher: null,
            NullNotificationSink.Instance,
            timeProvider ?? TimeProvider.System,
            new DiscordChannelOptions
            {
                Enabled = true,
                BotToken = new SensitiveString("test-token"),
                AllowedChannelIds = ["ch-1"]
            },
            NullLogger<DiscordChannel>.Instance,
            new ToolConfig
            {
                AudienceProfiles = TestDiscordGatewayDeps.DefaultAudienceProfiles
            },
            TestDiscordGatewayDeps.DefaultVisionCapableModel,
            TestDiscordGatewayDeps.NewTestPaths());
    }

    private sealed class FakeDiscordGatewayClient : IDiscordGatewayClient
    {
        private Func<string, Task>? _cleanReconnectRequired;

        public event Func<DiscordGatewayMessage, Task>? MessageReceived
        {
            add { }
            remove { }
        }

        public event Func<DiscordGatewayInteraction, Task>? InteractionReceived
        {
            add { }
            remove { }
        }

        public event Func<string, Task>? CleanReconnectRequired
        {
            add => _cleanReconnectRequired += value;
            remove => _cleanReconnectRequired -= value;
        }

        public bool IsConnected { get; set; }
        public bool IsReady { get; set; }
        public string? HealthDetail { get; set; }
        public DiscordUserId? BotUserId { get; set; }
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public Queue<bool> ConnectReadyResults { get; } = new();
        public bool RaiseCleanReconnectDuringFirstConnect { get; init; }

        public Task<DiscordGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot());

        public async Task<DiscordGatewaySnapshot> ConnectAsync(
            string botToken,
            CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            IsConnected = true;
            IsReady = NextConnectReady();
            HealthDetail = IsReady ? null : "Discord gateway connected but not ready.";
            BotUserId ??= new DiscordUserId("bot-1");

            if (RaiseCleanReconnectDuringFirstConnect && ConnectCount == 1 && _cleanReconnectRequired is not null)
                await _cleanReconnectRequired("Discord.Net resumed a previous gateway session.");

            return Snapshot();
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            IsConnected = false;
            IsReady = false;
            HealthDetail = "Discord gateway disconnected.";
            return Task.CompletedTask;
        }

        public Task RaiseCleanReconnectRequiredAsync(string reason) =>
            _cleanReconnectRequired?.Invoke(reason) ?? Task.CompletedTask;

        private bool NextConnectReady()
        {
            if (ConnectReadyResults.TryDequeue(out var ready))
                return ready;

            return true;
        }

        private DiscordGatewaySnapshot Snapshot() =>
            new(IsConnected, IsReady, HealthDetail, BotUserId);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
