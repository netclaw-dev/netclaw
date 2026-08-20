// -----------------------------------------------------------------------
// <copyright file="GatewayClientDisconnectShutdownTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels.Discord.Transport;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class GatewayClientDisconnectShutdownTests
{
    /// <summary>
    /// On SIGTERM, Akka's CLR shutdown hook can terminate the actor system
    /// before host shutdown reaches the channel. An Ask to a dead actor
    /// dead-letters and stalls for the full 35-second connect ask budget.
    /// DisconnectAsync must fast-fail when the actor system is already gone —
    /// nothing is left to drain (netclaw-dev/netclaw#2035).
    /// </summary>
    [Fact]
    public async Task Discord_disconnect_returns_immediately_when_actor_system_has_terminated()
    {
        using var system = ActorSystem.Create("discord-disconnect-shutdown-test");
        using var discordClient = new DiscordSocketClient(new DiscordSocketConfig());
        var client = new DiscordNetGatewayClient(
            system,
            discordClient,
            TimeProvider.System,
            NullLogger<DiscordNetGatewayClient>.Instance);

        // Simulate the SIGTERM race: the actor system terminates (killing the
        // lifecycle actor) before the channel's StopAsync reaches the transport.
        await system.Terminate();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await client.DisconnectAsync(CancellationToken.None);
        sw.Stop();

        // Must not burn the 35-second ask budget against a dead actor.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"DisconnectAsync took {sw.Elapsed} after system termination.");
    }
}
