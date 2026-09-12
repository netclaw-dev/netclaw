// -----------------------------------------------------------------------
// <copyright file="DiscordChannelShutdownContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class DiscordChannelShutdownContractTests : ChannelShutdownContractTests
{
    protected override IChannel CreateStoppableChannel()
    {
        var replyClient = new UnconfiguredDiscordReplyClient();

        return new DiscordChannel(
            system: null!,
            pipeline: new FailingSessionPipeline(new InvalidOperationException("not used")),
            ingressGate: new SessionIngressGate(),
            gatewayClient: new TimingOutGatewayClient(),
            replyClient: replyClient,
            channelRegistry: TestChannelRegistries.DiscordWithProcessingRenderer(replyClient),
            contentScanner: new NullContentScanner(),
            promptInjectionDetector: SafePromptInjectionDetector.Instance,
            httpClientFactory: new FakeHttpClientFactory(),
            threadHistoryFetcher: null,
            notificationSink: NullNotificationSink.Instance,
            timeProvider: TimeProvider.System,
            options: new DiscordChannelOptions
            {
                Enabled = true,
                BotToken = new SensitiveString("test-token"),
                AllowedChannelIds = ["ch-1"]
            },
            logger: NullLogger<DiscordChannel>.Instance,
            toolConfig: new ToolConfig
            {
                AudienceProfiles = TestDiscordGatewayDeps.DefaultAudienceProfiles
            },
            modelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance);
    }

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
