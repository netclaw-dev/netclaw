// -----------------------------------------------------------------------
// <copyright file="SlackChannelHealthContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using SlackNet;
using SlackNet.SocketMode;
using SlackNet.WebApi;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Slack implements only the base health contract: its socket-mode transport
/// is binary (connected after the channel's own connect path succeeds,
/// disconnected otherwise), so the snapshot-based connected-but-not-ready and
/// detail-propagation behaviors in <see cref="SnapshotChannelHealthContractTests"/>
/// do not apply.
/// </summary>
public sealed class SlackChannelHealthContractTests(ITestOutputHelper output)
    : ChannelHealthContractTests(output)
{
    private SlackChannel? _channel;

    protected override IChannel CreateChannel(bool enabled)
    {
        _channel = new SlackChannel(
            new FailingSessionPipeline(new InvalidOperationException("not used")),
            Sys,
            new FakeSlackApiClient(auth: new StubAuthApi()),
            new FakeSlackSocketModeClient(),
            new RecordingSlackReplyClient(),
            TestSlackGatewayDeps.DefaultChannelRegistry,
            new SessionIngressGate(),
            new NullContentScanner(),
            SafePromptInjectionDetector.Instance,
            new FakeHttpClientFactory(),
            NullNotificationSink.Instance,
            TimeProvider.System,
            new SlackChannelOptions
            {
                Enabled = enabled,
                BotToken = new SensitiveString("xoxb-test"),
                AppToken = new SensitiveString("xapp-test"),
                DefaultChannelId = "C-1",
                AllowedChannelIds = ["C-1"]
            },
            NullLogger<SlackChannel>.Instance,
            EmptyThreadHistoryFetcher.Instance,
            new ToolConfig
            {
                AudienceProfiles = TestSlackGatewayDeps.DefaultAudienceProfiles
            },
            TestSlackGatewayDeps.DefaultVisionCapableModel,
            TestSlackGatewayDeps.NewTestPaths());

        return _channel;
    }

    protected override async Task SetTransportStateAsync(bool connected, bool ready, string? healthDetail)
    {
        // Guard against future base-contract tests assuming a partial-ready
        // state Slack cannot represent — fail loud instead of silently
        // collapsing it to connected/disconnected.
        if (connected != ready || healthDetail is not null)
            throw new NotSupportedException(
                "Slack's socket-mode transport has no connected-but-not-ready state or snapshot detail.");

        // The only way Slack reaches the connected state is through its own
        // connect path; a freshly constructed channel is already disconnected.
        if (connected)
            await _channel!.StartAsync(CancellationToken.None);
    }

    private sealed class StubAuthApi : IAuthApi
    {
        public Task<bool> Revoke(bool test, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AuthTestResponse> Test(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthTestResponse { UserId = "UBOT" });

        public Task<AuthTeamsListResponse> TeamsList(
            string? cursor = null,
            bool includeIcon = false,
            int limit = 100,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSlackSocketModeClient : ISlackSocketModeClient
    {
        public bool Connected { get; private set; }

        public Task Connect(
            SocketModeConnectionOptions? connectionOptions = null,
            CancellationToken cancellationToken = default)
        {
            Connected = true;
            return Task.CompletedTask;
        }

        public void Disconnect() => Connected = false;

        public void Dispose()
        {
        }
    }
}
