// -----------------------------------------------------------------------
// <copyright file="ChannelHealthContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Channels;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Behavioral contract for <see cref="IChannel.GetHealthAsync"/>. Every channel
/// must report Healthy when its transport is connected and ready, Disconnected
/// (with a reason) when the transport is down, and Degraded when the channel is
/// disabled by configuration.
/// </summary>
public abstract class ChannelHealthContractTests : TestKit
{
    protected ChannelHealthContractTests(ITestOutputHelper output) : base(output: output) { }

    protected override Config? Config =>
        ConfigurationFactory.ParseString("akka.test.default-timeout = 5s");

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    /// <summary>Creates the channel wired to a controllable fake transport.</summary>
    protected abstract IChannel CreateChannel(bool enabled);

    /// <summary>
    /// Drives the fake transport into the given state. Always called after
    /// <see cref="CreateChannel"/> — Slack can only reach the connected state
    /// by running the channel's own connect path.
    /// </summary>
    protected abstract Task SetTransportStateAsync(bool connected, bool ready, string? healthDetail);

    [Fact]
    public async Task Healthy_when_transport_connected_and_ready()
    {
        var channel = CreateChannel(enabled: true);
        await SetTransportStateAsync(connected: true, ready: true, healthDetail: null);

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelHealthStatus.Healthy, health.Status);
        Assert.Null(health.Detail);
    }

    [Fact]
    public async Task Disconnected_when_transport_disconnected()
    {
        var channel = CreateChannel(enabled: true);
        await SetTransportStateAsync(connected: false, ready: false, healthDetail: null);

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelHealthStatus.Disconnected, health.Status);
        // Even without transport detail, the channel must say *why* it is offline.
        Assert.False(string.IsNullOrWhiteSpace(health.Detail));
    }

    [Fact]
    public async Task Degraded_when_channel_disabled()
    {
        var channel = CreateChannel(enabled: false);

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelHealthStatus.Degraded, health.Status);
        Assert.NotNull(health.Detail);
        Assert.Contains("disabled", health.Detail, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Additional health contract for channels whose transport exposes a
/// connected/ready snapshot with a health detail (Discord, Mattermost).
/// Slack's socket-mode transport is binary — there is no connected-but-not-ready
/// state and no snapshot detail — so its fixture implements only the base contract.
/// </summary>
public abstract class SnapshotChannelHealthContractTests : ChannelHealthContractTests
{
    protected SnapshotChannelHealthContractTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task Degraded_when_connected_but_not_ready_with_snapshot_detail()
    {
        var channel = CreateChannel(enabled: true);
        await SetTransportStateAsync(
            connected: true,
            ready: false,
            healthDetail: "Transport resumed a stale session.");

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelHealthStatus.Degraded, health.Status);
        Assert.Equal("Transport resumed a stale session.", health.Detail);
    }

    [Fact]
    public async Task Disconnected_detail_propagated_from_snapshot()
    {
        var channel = CreateChannel(enabled: true);
        await SetTransportStateAsync(
            connected: false,
            ready: false,
            healthDetail: "Socket closed by remote host.");

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelHealthStatus.Disconnected, health.Status);
        Assert.Equal("Socket closed by remote host.", health.Detail);
    }
}
