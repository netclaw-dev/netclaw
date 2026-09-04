// -----------------------------------------------------------------------
// <copyright file="SlackChannelHealthContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
/// Slack implements the base health contract with the live Socket Mode state.
/// The transport has no separate ready state or health detail.
/// </summary>
public sealed class SlackChannelHealthContractTests(ITestOutputHelper output)
    : ChannelHealthContractTests(output)
{
    private SlackChannel? _channel;
    private FakeSlackSocketModeClient? _socketModeClient;
    private FakeTimeProvider? _timeProvider;
    private RecordingNotificationSink? _notificationSink;

    protected override IChannel CreateChannel(bool enabled)
    {
        _socketModeClient = new FakeSlackSocketModeClient();
        _timeProvider = new FakeTimeProvider();
        _notificationSink = new RecordingNotificationSink();
        _channel = new SlackChannel(
            new FailingSessionPipeline(new InvalidOperationException("not used")),
            Sys,
            new FakeSlackApiClient(auth: new StubAuthApi()),
            _socketModeClient,
            new RecordingSlackReplyClient(),
            TestSlackGatewayDeps.DefaultChannelRegistry,
            new SessionIngressGate(),
            new NullContentScanner(),
            SafePromptInjectionDetector.Instance,
            new FakeHttpClientFactory(),
            _notificationSink,
            _timeProvider,
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
            Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance);

        return _channel;
    }

    [Fact]
    public async Task Disconnected_when_live_transport_drops_after_start()
    {
        var channel = CreateChannel(enabled: true);
        await channel.StartAsync(TestContext.Current.CancellationToken);

        _socketModeClient!.DropConnection();

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelHealthStatus.Disconnected, health.Status);
        Assert.Equal("Slack socket mode disconnected.", health.Detail);
    }

    [Fact]
    public async Task Supervisor_reconnects_after_live_transport_drops()
    {
        var channel = CreateChannel(enabled: true);
        await channel.StartAsync(TestContext.Current.CancellationToken);
        _socketModeClient!.DropConnection();

        _timeProvider!.Advance(SlackChannel.ConnectionCheckInterval);
        await _socketModeClient.WaitForConnectCountAsync(
            expectedCount: 2,
            TestContext.Current.CancellationToken);

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ChannelHealthStatus.Healthy, health.Status);
        Assert.Contains(
            _notificationSink!.Alerts,
            alert => alert.Category == AlertType.ChannelDisconnected);
        Assert.Contains(
            _notificationSink.Alerts,
            alert => alert.Category == AlertType.ChannelReconnected
                     && alert.Type == "channel.reconnected");
    }

    [Fact]
    public async Task Supervisor_backs_off_then_recovers_after_reconnect_failure()
    {
        var channel = CreateChannel(enabled: true);
        await channel.StartAsync(TestContext.Current.CancellationToken);
        _socketModeClient!.FailNextConnections(1);
        _socketModeClient.DropConnection();

        _timeProvider!.Advance(SlackChannel.ConnectionCheckInterval);
        await _socketModeClient.WaitForConnectCountAsync(
            expectedCount: 2,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, _socketModeClient.ConnectCount);
        Assert.Equal(
            ChannelHealthStatus.Disconnected,
            (await channel.GetHealthAsync(TestContext.Current.CancellationToken)).Status);

        _timeProvider.Advance(SlackChannel.ComputeReconnectDelay(1) - TimeSpan.FromSeconds(1));
        Assert.Equal(2, _socketModeClient.ConnectCount);

        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        await _socketModeClient.WaitForConnectCountAsync(
            expectedCount: 3,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, _socketModeClient.ConnectCount);
        Assert.Equal(
            ChannelHealthStatus.Healthy,
            (await channel.GetHealthAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(6, 160)]
    [InlineData(7, 300)]
    [InlineData(20, 300)]
    public void Reconnect_delay_is_bounded(int failureCount, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            SlackChannel.ComputeReconnectDelay(failureCount));
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

    protected override async Task AfterAllAsync()
    {
        if (_channel is not null)
            await _channel.StopAsync(CancellationToken.None);

        await base.AfterAllAsync();
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
        private readonly Lock _sync = new();
        private TaskCompletionSource _connectChanged = NewSignal();
        private int _connectCount;
        private int _failNextConnections;
        private volatile bool _connected;

        public bool Connected => _connected;

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public Task Connect(
            SocketModeConnectionOptions? connectionOptions = null,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource signal;
            bool mustFail;
            lock (_sync)
            {
                Interlocked.Increment(ref _connectCount);
                mustFail = _failNextConnections > 0;
                if (mustFail)
                    _failNextConnections--;
                else
                    _connected = true;

                signal = _connectChanged;
                _connectChanged = NewSignal();
            }

            signal.TrySetResult();
            if (mustFail)
                throw new HttpRequestException("Test Socket Mode connection failed.");

            return Task.CompletedTask;
        }

        public void Disconnect() => _connected = false;

        public void DropConnection() => _connected = false;

        public void FailNextConnections(int count)
        {
            lock (_sync)
                _failNextConnections = count;
        }

        public async Task WaitForConnectCountAsync(int expectedCount, CancellationToken cancellationToken)
        {
            while (ConnectCount < expectedCount)
            {
                Task signal;
                lock (_sync)
                {
                    if (ConnectCount >= expectedCount)
                        return;

                    signal = _connectChanged.Task;
                }

                await signal.WaitAsync(cancellationToken);
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingNotificationSink : IOperationalNotificationSink
    {
        private readonly ConcurrentQueue<OperationalAlert> _alerts = new();

        public IReadOnlyCollection<OperationalAlert> Alerts => _alerts.ToArray();

        public void Emit(OperationalAlert alert) => _alerts.Enqueue(alert);
    }
}
