// -----------------------------------------------------------------------
// <copyright file="GatewayActorKeyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Hosting;
using Xunit;

namespace Netclaw.Actors.Tests.Hosting;

/// <summary>
/// Smoke tests for the gateway marker types introduced by
/// reminder-session-reentry. Full end-to-end wiring is covered by the
/// Section 10 Slack + SignalR integration tests.
/// </summary>
public sealed class GatewayActorKeyTests : TestKit
{
    public GatewayActorKeyTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    [Fact]
    public void SlackGatewayActorKey_can_be_registered_and_resolved()
    {
        var probe = CreateTestProbe("slack-gateway-stub");
        var registry = ActorRegistry.For(Sys);

        registry.Register<SlackGatewayActorKey>(probe.Ref);

        var resolved = registry.Get<SlackGatewayActorKey>();
        Assert.Same(probe.Ref, resolved);
    }

    [Fact]
    public void SignalRGatewayActorKey_can_be_registered_and_resolved()
    {
        var probe = CreateTestProbe("signalr-gateway-stub");
        var registry = ActorRegistry.For(Sys);

        registry.Register<SignalRGatewayActorKey>(probe.Ref);

        var resolved = registry.Get<SignalRGatewayActorKey>();
        Assert.Same(probe.Ref, resolved);
    }

    [Fact]
    public void DiscordGatewayActorKey_can_be_registered_and_resolved()
    {
        var probe = CreateTestProbe("discord-gateway-stub");
        var registry = ActorRegistry.For(Sys);

        registry.Register<DiscordGatewayActorKey>(probe.Ref);

        var resolved = registry.Get<DiscordGatewayActorKey>();
        Assert.Same(probe.Ref, resolved);
    }
}
