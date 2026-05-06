// -----------------------------------------------------------------------
// <copyright file="McpReconnectionServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpReconnectionServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeNotificationSink _sink = new();
    private readonly FakeMcpReconnectable _reconnectable = new();

    private McpReconnectionService CreateService() =>
        new(_reconnectable, _sink, _time, NullLogger<McpReconnectionService>.Instance);

    [Fact]
    public async Task SkipsNonUnreachableServers()
    {
        _reconnectable.SetStatus("connected-server", McpConnectionState.Connected, 5);
        _reconnectable.SetStatus("auth-server", McpConnectionState.AwaitingAuth);
        _reconnectable.SetStatus("failed-auth", McpConnectionState.AuthFailed);
        _reconnectable.SetStatus("disabled-server", McpConnectionState.Disabled);

        var service = CreateService();
        await service.CheckAndReconnectAsync(CancellationToken.None);

        Assert.Equal(0, _reconnectable.ReconnectCallCount);
    }

    [Fact]
    public async Task RetriesUnreachableServerOnFirstTick()
    {
        _reconnectable.SetStatus("memorizer", McpConnectionState.Unreachable);
        _reconnectable.OnReconnect = (_, _) =>
        {
            _reconnectable.SetStatus("memorizer", McpConnectionState.Connected, 10);
            return Task.FromResult(true);
        };

        var service = CreateService();
        await service.CheckAndReconnectAsync(CancellationToken.None);

        Assert.Equal(1, _reconnectable.ReconnectCallCount);
        Assert.Single(_sink.Alerts);
        Assert.Equal(AlertType.McpServerReconnected, _sink.Alerts[0].Category);
        Assert.Contains("memorizer", _sink.Alerts[0].Summary);
    }

    [Fact]
    public async Task BacksOffAfterFailedReconnect()
    {
        _reconnectable.SetStatus("memorizer", McpConnectionState.Unreachable);
        _reconnectable.OnReconnect = (_, _) => Task.FromResult(false);

        var service = CreateService();

        // First tick: attempt 1 (immediate, failureCount=0)
        await service.CheckAndReconnectAsync(CancellationToken.None);
        Assert.Equal(1, _reconnectable.ReconnectCallCount);

        // Advance 15s — still within 30s backoff window
        _time.Advance(TimeSpan.FromSeconds(15));
        await service.CheckAndReconnectAsync(CancellationToken.None);
        Assert.Equal(1, _reconnectable.ReconnectCallCount);

        // Advance to 31s total — past the 30s backoff
        _time.Advance(TimeSpan.FromSeconds(16));
        await service.CheckAndReconnectAsync(CancellationToken.None);
        Assert.Equal(2, _reconnectable.ReconnectCallCount);
    }

    [Fact]
    public async Task BackoffResetsOnSuccess()
    {
        _reconnectable.SetStatus("memorizer", McpConnectionState.Unreachable);

        var callCount = 0;
        _reconnectable.OnReconnect = (_, _) =>
        {
            callCount++;
            if (callCount >= 3)
            {
                _reconnectable.SetStatus("memorizer", McpConnectionState.Connected, 10);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        };

        var service = CreateService();

        // Fail twice to build up backoff
        await service.CheckAndReconnectAsync(CancellationToken.None); // attempt 1
        _time.Advance(TimeSpan.FromSeconds(31));
        await service.CheckAndReconnectAsync(CancellationToken.None); // attempt 2

        // Third attempt succeeds
        _time.Advance(TimeSpan.FromSeconds(61));
        await service.CheckAndReconnectAsync(CancellationToken.None); // attempt 3

        Assert.Single(_sink.Alerts);
        Assert.Equal(AlertType.McpServerReconnected, _sink.Alerts[0].Category);

        // Simulate server going unreachable again — backoff should be reset
        _reconnectable.SetStatus("memorizer", McpConnectionState.Unreachable);
        _reconnectable.OnReconnect = (_, _) => Task.FromResult(false);

        // Should retry immediately (no stale backoff)
        _time.Advance(TimeSpan.FromSeconds(1));
        var countBefore = _reconnectable.ReconnectCallCount;
        await service.CheckAndReconnectAsync(CancellationToken.None);
        Assert.Equal(countBefore + 1, _reconnectable.ReconnectCallCount);
    }

    [Fact]
    public async Task HandlesExceptionsWithoutCrashing()
    {
        _reconnectable.SetStatus("memorizer", McpConnectionState.Unreachable);
        _reconnectable.OnReconnect = (_, _) => throw new HttpRequestException("Connection refused");

        var service = CreateService();
        await service.CheckAndReconnectAsync(CancellationToken.None);

        Assert.Equal(1, _reconnectable.ReconnectCallCount);
        Assert.Empty(_sink.Alerts);

        // Should still back off after exception
        _time.Advance(TimeSpan.FromSeconds(15));
        await service.CheckAndReconnectAsync(CancellationToken.None);
        Assert.Equal(1, _reconnectable.ReconnectCallCount);
    }

    [Fact]
    public async Task CleansUpBackoffWhenServerRecoveredExternally()
    {
        _reconnectable.SetStatus("memorizer", McpConnectionState.Unreachable);
        _reconnectable.OnReconnect = (_, _) => Task.FromResult(false);

        var service = CreateService();

        // Build up some backoff
        await service.CheckAndReconnectAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(31));
        await service.CheckAndReconnectAsync(CancellationToken.None);

        // External code reconnected the server
        _reconnectable.SetStatus("memorizer", McpConnectionState.Connected, 10);
        await service.CheckAndReconnectAsync(CancellationToken.None);

        // Now it goes unreachable again — should retry immediately (backoff was cleaned)
        _reconnectable.SetStatus("memorizer", McpConnectionState.Unreachable);
        _reconnectable.OnReconnect = (_, _) => Task.FromResult(false);

        _time.Advance(TimeSpan.FromSeconds(1));
        var countBefore = _reconnectable.ReconnectCallCount;
        await service.CheckAndReconnectAsync(CancellationToken.None);
        Assert.Equal(countBefore + 1, _reconnectable.ReconnectCallCount);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 30_000)]
    [InlineData(2, 60_000)]
    [InlineData(3, 120_000)]
    [InlineData(4, 240_000)]
    [InlineData(5, 300_000)]
    [InlineData(10, 300_000)]
    public void ComputeBackoffMs_ReturnsExpectedValues(int failureCount, long expectedMs)
    {
        Assert.Equal(expectedMs, McpReconnectionService.ComputeBackoffMs(failureCount));
    }

    private sealed class FakeMcpReconnectable : IMcpReconnectable
    {
        private readonly Dictionary<McpServerName, McpServerStatus> _statuses = new();
        private int _reconnectCallCount;

        public int ReconnectCallCount => Volatile.Read(ref _reconnectCallCount);

        public Func<McpServerName, CancellationToken, Task<bool>>? OnReconnect { get; set; }

        public void SetStatus(string name, McpConnectionState state, int toolCount = 0)
        {
            var serverName = new McpServerName(name);
            _statuses[serverName] = new McpServerStatus(
                serverName, state, toolCount,
                state == McpConnectionState.Unreachable ? "test error" : null);
        }

        public IReadOnlyDictionary<McpServerName, McpServerStatus> GetServerStatuses() => _statuses;

        public async Task<bool> TryReconnectAsync(McpServerName serverName, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reconnectCallCount);
            if (OnReconnect is not null)
                return await OnReconnect(serverName, ct);
            return false;
        }
    }

    private sealed class FakeNotificationSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];
        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }
}
