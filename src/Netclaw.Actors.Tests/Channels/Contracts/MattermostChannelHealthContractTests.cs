// -----------------------------------------------------------------------
// <copyright file="MattermostChannelHealthContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Mattermost;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class MattermostChannelHealthContractTests(ITestOutputHelper output)
    : SnapshotChannelHealthContractTests(output)
{
    private readonly FakeMattermostGatewayClient _gateway = new();

    protected override IChannel CreateChannel(bool enabled)
    {
        return new MattermostChannel(
            Sys,
            new FailingSessionPipeline(new InvalidOperationException("not used")),
            new SessionIngressGate(),
            _gateway,
            new RecordingMattermostReplyClient(),
            new NullContentScanner(),
            SafePromptInjectionDetector.Instance,
            new FakeHttpClientFactory(),
            null,
            NullNotificationSink.Instance,
            TimeProvider.System,
            new MattermostChannelOptions
            {
                Enabled = enabled,
                ServerUrl = "https://mattermost.example.com",
                BotToken = new SensitiveString("test-token"),
                AllowedChannelIds = ["ch-1"]
            },
            NullLogger<MattermostChannel>.Instance,
            new ToolConfig
            {
                AudienceProfiles = TestMattermostGatewayDeps.DefaultAudienceProfiles
            },
            TestMattermostGatewayDeps.DefaultVisionCapableModel,
            Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance);
    }

    protected override Task SetTransportStateAsync(bool connected, bool ready, string? healthDetail)
    {
        _gateway.IsConnected = connected;
        _gateway.IsReady = ready;
        _gateway.HealthDetail = healthDetail;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Snapshot-only fake: health checks never connect or disconnect the
    /// transport, so those paths fail loud.
    /// </summary>
    private sealed class FakeMattermostGatewayClient : IMattermostGatewayClient
    {
        public event Func<MattermostGatewayMessage, Task>? MessageReceived { add { } remove { } }
        public event Func<string, Task>? CleanReconnectRequired { add { } remove { } }
        public event Func<MattermostGatewaySnapshot, Task>? ConnectionRestored { add { } remove { } }

        public bool IsConnected { get; set; }
        public bool IsReady { get; set; }
        public string? HealthDetail { get; set; }
        public MattermostUserId? BotUserId => new("bot-1");
        public string? BotUsername => "netclaw";

        public Task<MattermostGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MattermostGatewaySnapshot(
                IsConnected, IsReady, HealthDetail, BotUserId, BotUsername));

        public Task<MattermostGatewaySnapshot> ConnectAsync(
            string serverUrl, string botToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Health contract tests never connect the transport.");

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Health contract tests never disconnect the transport.");
    }
}
