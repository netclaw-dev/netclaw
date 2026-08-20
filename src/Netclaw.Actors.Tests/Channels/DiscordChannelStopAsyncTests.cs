// -----------------------------------------------------------------------
// <copyright file="DiscordChannelStopAsyncTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordChannelStopAsyncTests
{
    /// <summary>
    /// <see cref="DiscordChannel.StopAsync"/> runs as a hosted-service stop, so
    /// any exception it throws reaches Host.StopAsync and is recorded as a
    /// daemon-main crash. On SIGTERM, Akka's CLR shutdown hook runs
    /// CoordinatedShutdown concurrently with host shutdown, so the gateway
    /// lifecycle actor is often dead before the channel's stop runs. The
    /// disconnect Ask then dead-letters and times out after 35 seconds. That
    /// teardown race is normal, so the disconnect failure must be logged, not
    /// thrown. Today the `DisconnectAsync` await is unguarded and a clean
    /// `netclaw daemon stop` of an idle daemon is recorded as a crash
    /// (observed in production on 2026-08-20).
    /// </summary>
    [Fact]
    public async Task StopAsync_does_not_propagate_gateway_disconnect_failure()
    {
        var channel = CreateStoppableChannel(new TimingOutGatewayClient());

        await channel.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Builds a channel with only the dependencies the stop path touches. The
    /// gateway actor is never started, so StopAsync exercises the transport
    /// disconnect and nothing else.
    /// </summary>
    private static DiscordChannel CreateStoppableChannel(IDiscordGatewayClient gatewayClient)
        => new(
            system: null!,
            pipeline: null!,
            ingressGate: null!,
            gatewayClient: gatewayClient,
            replyClient: null!,
            channelRegistry: null!,
            contentScanner: null!,
            promptInjectionDetector: SafePromptInjectionDetector.Instance,
            httpClientFactory: null!,
            threadHistoryFetcher: null,
            notificationSink: null!,
            timeProvider: TimeProvider.System,
            options: new DiscordChannelOptions(),
            logger: NullLogger<DiscordChannel>.Instance,
            toolConfig: new ToolConfig(),
            modelCapabilities: new ModelCapabilities(),
            paths: null!);

    /// <summary>
    /// Reproduces the transport state during the SIGTERM race: the lifecycle
    /// actor is already dead, so the disconnect Ask times out at its full
    /// 35-second budget.
    /// </summary>
    private sealed class TimingOutGatewayClient : IDiscordGatewayClient
    {
        public event Func<DiscordGatewayMessage, Task>? MessageReceived { add { } remove { } }

        public event Func<DiscordGatewayInteraction, Task>? InteractionReceived { add { } remove { } }

        public event Func<string, Task>? CleanReconnectRequired { add { } remove { } }

        public event Func<DiscordGatewaySnapshot, Task>? ConnectionRestored { add { } remove { } }

        public Task<DiscordGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used by the stop path.");

        public Task<DiscordGatewaySnapshot> ConnectAsync(string botToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used by the stop path.");

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
            => Task.FromException(new AskTimeoutException("Timeout after 35.00 seconds"));
    }
}
