// -----------------------------------------------------------------------
// <copyright file="TelegramChannelStartupTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Telegram.Bot.Exceptions;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramChannelStartupTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Transient_start_failure_retries_and_becomes_healthy()
    {
        var timeProvider = new FakeTimeProvider();
        var failing = new TelegramTransportTests.FakeTelegramBotApiClient { GetMeFailure = new HttpRequestException("connection reset by peer") };
        var succeeding = new TelegramTransportTests.FakeTelegramBotApiClient();
        var clients = new Queue<TelegramTransportTests.FakeTelegramBotApiClient>([failing, succeeding]);
        var builds = 0;
        var transport = CreateTransport((_, _) =>
        {
            builds++;
            return clients.Dequeue();
        });
        var channel = CreateChannel(timeProvider, transport);

        await channel.StartAsync(TestContext.Current.CancellationToken);

        var afterFailure = await channel.GetHealthAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ChannelHealthStatus.Disconnected, afterFailure.Status);
        Assert.Contains("connection reset", afterFailure.Detail);
        Assert.Equal(1, builds);

        timeProvider.Advance(TelegramChannel.RetryCheckInterval);

        await AwaitAssertAsync(async () =>
        {
            var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ChannelHealthStatus.Healthy, health.Status);
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, failing.GetMeCalls);
        Assert.Equal(1, succeeding.GetMeCalls);
        Assert.Equal(3, succeeding.EventSubscriptionCount);
        Assert.Equal(2, builds);

        for (var i = 0; i < 3; i++)
            timeProvider.Advance(TelegramChannel.RetryCheckInterval);

        await AwaitAssertAsync(async () =>
        {
            var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ChannelHealthStatus.Healthy, health.Status);
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, builds);
        Assert.Equal(1, succeeding.GetMeCalls);
    }

    [Fact]
    public async Task Fatal_token_failure_stays_offline_without_retry()
    {
        var timeProvider = new FakeTimeProvider();
        var client = new TelegramTransportTests.FakeTelegramBotApiClient { GetMeFailure = new ApiRequestException("Unauthorized", 401) };
        var builds = 0;
        var transport = CreateTransport((_, _) =>
        {
            builds++;
            return client;
        });
        var channel = CreateChannel(timeProvider, transport);

        await channel.StartAsync(TestContext.Current.CancellationToken);

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ChannelHealthStatus.Disconnected, health.Status);
        Assert.Contains("Unauthorized", health.Detail);
        Assert.Equal(1, builds);

        timeProvider.Advance(TimeSpan.FromMinutes(10));

        var after = await channel.GetHealthAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ChannelHealthStatus.Disconnected, after.Status);
        Assert.Contains("Unauthorized", after.Detail);
        Assert.Equal(1, client.GetMeCalls);
        Assert.Equal(1, builds);
    }

    private TelegramChannel CreateChannel(FakeTimeProvider timeProvider, TelegramTransport transport) =>
        new(
            pipeline: null!,
            ingressGate: null!,
            actorSystem: Sys,
            actorRegistry: ActorRegistry.For(Sys),
            options: ValidOptions(),
            transport: transport,
            logger: NullLogger<TelegramChannel>.Instance,
            contentScanner: null!,
            toolConfig: new ToolConfig(),
            modelCapabilities: new ModelCapabilities(),
            storageResolver: null!,
            channelRegistry: null!,
            timeProvider: timeProvider);

    private static TelegramChannelOptions ValidOptions() => new()
    {
        Enabled = true,
        BotToken = new SensitiveString("12345:test-token"),
    };

    private static TelegramTransport CreateTransport(TelegramBotClientFactory factory) =>
        new(ValidOptions(), NullLogger<TelegramTransport>.Instance, factory);
}

public sealed class TelegramRetryBackoffTests
{
    [Fact]
    public void Zero_failures_yield_no_delay() =>
        Assert.Equal(TimeSpan.Zero, TelegramChannel.ComputeRetryDelay(0));

    [Fact]
    public void First_retry_uses_the_check_interval() =>
        Assert.Equal(TimeSpan.FromSeconds(5), TelegramChannel.ComputeRetryDelay(1));

    [Fact]
    public void Retry_delay_doubles_per_failure() =>
        Assert.Equal(TimeSpan.FromSeconds(10), TelegramChannel.ComputeRetryDelay(2));

    [Fact]
    public void Retry_delay_is_capped_at_five_minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(5), TelegramChannel.ComputeRetryDelay(50));
}
