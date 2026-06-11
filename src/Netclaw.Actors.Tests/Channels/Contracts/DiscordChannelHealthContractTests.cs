// -----------------------------------------------------------------------
// <copyright file="DiscordChannelHealthContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class DiscordChannelHealthContractTests(ITestOutputHelper output)
    : SnapshotChannelHealthContractTests(output)
{
    private readonly FakeDiscordGatewayClient _gateway = new();

    protected override IChannel CreateChannel(bool enabled)
    {
        var replyClient = new UnconfiguredDiscordReplyClient();

        return new DiscordChannel(
            Sys,
            new FailingSessionPipeline(new InvalidOperationException("not used")),
            new SessionIngressGate(),
            _gateway,
            replyClient,
            TestChannelRegistries.DiscordWithProcessingRenderer(replyClient),
            new NullContentScanner(),
            SafePromptInjectionDetector.Instance,
            new FakeHttpClientFactory(),
            null,
            NullNotificationSink.Instance,
            TimeProvider.System,
            new DiscordChannelOptions
            {
                Enabled = enabled,
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
    private sealed class FakeDiscordGatewayClient : IDiscordGatewayClient
    {
        public event Func<DiscordGatewayMessage, Task>? MessageReceived { add { } remove { } }
        public event Func<DiscordGatewayInteraction, Task>? InteractionReceived { add { } remove { } }
        public event Func<string, Task>? CleanReconnectRequired { add { } remove { } }
        public event Func<DiscordGatewaySnapshot, Task>? ConnectionRestored { add { } remove { } }

        public bool IsConnected { get; set; }
        public bool IsReady { get; set; }
        public string? HealthDetail { get; set; }

        public Task<DiscordGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscordGatewaySnapshot(
                IsConnected, IsReady, HealthDetail, new DiscordUserId("bot-1")));

        public Task<DiscordGatewaySnapshot> ConnectAsync(string botToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Health contract tests never connect the transport.");

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Health contract tests never disconnect the transport.");
    }
}
