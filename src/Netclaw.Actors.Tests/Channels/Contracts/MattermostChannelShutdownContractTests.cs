// -----------------------------------------------------------------------
// <copyright file="MattermostChannelShutdownContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Mattermost;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class MattermostChannelShutdownContractTests : ChannelShutdownContractTests
{
    protected override IChannel CreateStoppableChannel()
    {
        return new MattermostChannel(
            system: null!,
            pipeline: new FailingSessionPipeline(new InvalidOperationException("not used")),
            ingressGate: new SessionIngressGate(),
            gatewayClient: new TimingOutGatewayClient(),
            replyClient: new RecordingMattermostReplyClient(),
            contentScanner: new NullContentScanner(),
            promptInjectionDetector: SafePromptInjectionDetector.Instance,
            httpClientFactory: new FakeHttpClientFactory(),
            threadHistoryFetcher: null,
            notificationSink: NullNotificationSink.Instance,
            timeProvider: TimeProvider.System,
            options: new MattermostChannelOptions
            {
                Enabled = true,
                ServerUrl = "https://mattermost.example.com",
                BotToken = new SensitiveString("test-token"),
                AllowedChannelIds = ["ch-1"]
            },
            logger: NullLogger<MattermostChannel>.Instance,
            toolConfig: new ToolConfig
            {
                AudienceProfiles = TestMattermostGatewayDeps.DefaultAudienceProfiles
            },
            modelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
            Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance);
    }

    /// <summary>
    /// Reproduces the transport state during the SIGTERM race: the lifecycle
    /// actor is already dead, so the disconnect Ask times out at its full
    /// 35-second budget.
    /// </summary>
    private sealed class TimingOutGatewayClient : IMattermostGatewayClient
    {
        public event Func<MattermostGatewayMessage, Task>? MessageReceived { add { } remove { } }

        public event Func<string, Task>? CleanReconnectRequired { add { } remove { } }

        public event Func<MattermostGatewaySnapshot, Task>? ConnectionRestored { add { } remove { } }

        public bool IsConnected => false;

        public bool IsReady => false;

        public MattermostUserId? BotUserId => null;

        public string? BotUsername => null;

        public Task<MattermostGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used by the stop path.");

        public Task<MattermostGatewaySnapshot> ConnectAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used by the stop path.");

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
            => Task.FromException(new AskTimeoutException("Timeout after 35.00 seconds"));
    }
}
