// -----------------------------------------------------------------------
// <copyright file="ChannelShutdownContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Behavioral contract for <see cref="IChannel.StopAsync"/> during daemon
/// shutdown. Every channel runs as a hosted-service stop, so any exception it
/// throws reaches <c>Host.StopAsync</c> and is recorded as a
/// <c>daemon-main</c> crash. On SIGTERM, Akka's CLR shutdown hook can
/// terminate the actor system before host shutdown reaches the channel — the
/// transport disconnect then dead-letters and times out after its full ask
/// budget. That teardown race is normal, so the disconnect failure must be
/// logged, not thrown (netclaw-dev/netclaw#2035).
/// </summary>
public abstract class ChannelShutdownContractTests
{
    /// <summary>
    /// A disconnect that fails exactly like the SIGTERM race must not make
    /// <see cref="IChannel.StopAsync"/> throw. The failure is expected
    /// teardown noise — the process is going down either way.
    /// </summary>
    [Fact]
    public async Task StopAsync_does_not_propagate_gateway_disconnect_failure()
    {
        var channel = CreateStoppableChannel();

        await channel.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Builds the channel with only the dependencies the stop path touches,
    /// wired to a fake transport whose disconnect fails with the dead-actor
    /// timeout. The gateway actor is never started, so StopAsync exercises
    /// the transport disconnect and nothing else.
    /// </summary>
    protected abstract IChannel CreateStoppableChannel();

    /// <summary>
    /// A fake transport whose disconnect fails like a dead lifecycle actor.
    /// </summary>
    protected abstract class FailingDisconnectGatewayClient;
}
