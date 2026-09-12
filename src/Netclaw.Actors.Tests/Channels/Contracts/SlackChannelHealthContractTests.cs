// -----------------------------------------------------------------------
// <copyright file="SlackChannelHealthContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Microsoft.Extensions.Logging;
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
            new ReconnectFailureLogger(TestActor),
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supervisor_reconnects_after_live_transport_drops(bool deliverBeforeWait)
    {
        var ct = TestContext.Current.CancellationToken;
        var channel = CreateChannel(enabled: true);
        using var releaseDelivery = new ManualResetEventSlim();
        var deliveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _notificationSink!.BeforeReconnectEmit = () =>
        {
            deliveryEntered.TrySetResult();
            releaseDelivery.Wait(ct);
        };

        // Keep the supervisor off the test context so a held sink cannot block the test continuation.
        await Task.Run(() => channel.StartAsync(ct), ct);
        _socketModeClient!.DropConnection();
        var tick = Task.Run(() => _timeProvider!.Advance(SlackChannel.ConnectionCheckInterval), ct);

        try
        {
            await deliveryEntered.Task.WaitAsync(RemainingOrDefault, ct);
            Assert.Equal(ChannelHealthStatus.Healthy, (await channel.GetHealthAsync(ct)).Status);
            Assert.Contains(_notificationSink.Alerts, alert => alert.Category == AlertType.ChannelDisconnected);
            // This is the CI race: connection success does not imply notification delivery.
            Assert.DoesNotContain(_notificationSink.Alerts, alert => alert.Category == AlertType.ChannelReconnected);

            Task<OperationalAlert> delivery;
            if (deliverBeforeWait)
            {
                releaseDelivery.Set();
                await tick;
                delivery = _notificationSink.WaitForReconnectAsync(RemainingOrDefault, ct);
                Assert.True(delivery.IsCompletedSuccessfully);
            }
            else
            {
                delivery = _notificationSink.WaitForReconnectAsync(RemainingOrDefault, ct);
                Assert.False(delivery.IsCompleted);
                releaseDelivery.Set();
            }

            var alert = await delivery;
            Assert.Equal(AlertType.ChannelReconnected, alert.Category);
            Assert.Equal("channel.reconnected", alert.Type);
            Assert.Contains(alert, _notificationSink.Alerts);
        }
        finally
        {
            releaseDelivery.Set();
            await tick;
        }
    }

    [Fact]
    public async Task Supervisor_backs_off_then_recovers_after_reconnect_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var channel = CreateChannel(enabled: true);
        // Start and advance on workers so every timer wait stays off the test context.
        await Task.Run(() => channel.StartAsync(ct), ct);
        _socketModeClient!.FailNextConnections(2);
        _socketModeClient.DropConnection();

        await Task.Run(() => _timeProvider!.Advance(SlackChannel.ConnectionCheckInterval), ct);
        await ExpectMsgAsync(SlackChannel.ComputeReconnectDelay(1), cancellationToken: ct);
        Assert.Equal(2, _socketModeClient.ConnectCount);
        Assert.Equal(ChannelHealthStatus.Disconnected, (await channel.GetHealthAsync(ct)).Status);

        await Task.Run(() => _timeProvider!.Advance(SlackChannel.ConnectionCheckInterval), ct);
        await ExpectMsgAsync(SlackChannel.ComputeReconnectDelay(2), cancellationToken: ct);
        Assert.Equal(3, _socketModeClient.ConnectCount);

        // The second failure requires ten seconds. The intervening five-second poll must not connect.
        await Task.Run(() => _timeProvider!.Advance(SlackChannel.ConnectionCheckInterval), ct);
        Assert.Equal(3, _socketModeClient.ConnectCount);
        Assert.Equal(ChannelHealthStatus.Disconnected, (await channel.GetHealthAsync(ct)).Status);

        await Task.Run(() => _timeProvider!.Advance(SlackChannel.ConnectionCheckInterval), ct);
        await _notificationSink!.WaitForReconnectAsync(RemainingOrDefault, ct);
        Assert.Equal(4, _socketModeClient.ConnectCount);
        Assert.Equal(ChannelHealthStatus.Healthy, (await channel.GetHealthAsync(ct)).Status);
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
        private int _connectCount;
        private int _failNextConnections;
        private volatile bool _connected;

        public bool Connected => _connected;

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public Task Connect(
            SocketModeConnectionOptions? connectionOptions = null,
            CancellationToken cancellationToken = default)
        {
            bool mustFail;
            lock (_sync)
            {
                Interlocked.Increment(ref _connectCount);
                mustFail = _failNextConnections > 0;
                if (mustFail)
                    _failNextConnections--;
                else
                    _connected = true;

            }

            if (mustFail)
                throw new HttpRequestException("Test Socket Mode connection failed.");

            return Task.CompletedTask;
        }

        public void Disconnect() => _connected = false;

        public Task DisconnectAsync()
        {
            _connected = false;
            return Task.CompletedTask;
        }

        public void DropConnection() => _connected = false;

        public void FailNextConnections(int count)
        {
            lock (_sync)
                _failNextConnections = count;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingNotificationSink : IOperationalNotificationSink
    {
        private readonly ConcurrentQueue<OperationalAlert> _alerts = new();
        private readonly TaskCompletionSource<OperationalAlert> _reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<OperationalAlert> Alerts => _alerts.ToArray();

        public Action? BeforeReconnectEmit { get; set; }

        public Task<OperationalAlert> WaitForReconnectAsync(TimeSpan timeout, CancellationToken ct) => _reconnected.Task.WaitAsync(timeout, ct);

        public void Emit(OperationalAlert alert)
        {
            if (alert.Category == AlertType.ChannelReconnected)
                BeforeReconnectEmit?.Invoke();

            _alerts.Enqueue(alert);
            if (alert.Category == AlertType.ChannelReconnected)
                _reconnected.TrySetResult(alert);
        }
    }

    private sealed class ReconnectFailureLogger(IActorRef observer) : ILogger<SlackChannel>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // The supervisor logs RetryDelay only after it commits the next attempt deadline.
            if (logLevel != LogLevel.Warning || state is not IEnumerable<KeyValuePair<string, object?>> fields)
                return;

            foreach (var (key, value) in fields)
            {
                if (key == "RetryDelay" && value is TimeSpan delay)
                    observer.Tell(delay);
            }
        }
    }

}
