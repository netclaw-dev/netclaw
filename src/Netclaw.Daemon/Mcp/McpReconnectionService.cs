// -----------------------------------------------------------------------
// <copyright file="McpReconnectionService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal sealed class McpReconnectionService : BackgroundService
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    internal const int BaseBackoffSeconds = 30;
    internal const int MaxBackoffSeconds = 300;

    private readonly IMcpReconnectable _mcpReconnectable;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpReconnectionService> _logger;

    private readonly ConcurrentDictionary<McpServerName, BackoffState> _backoff = new();

    public McpReconnectionService(
        IMcpReconnectable mcpReconnectable,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider,
        ILogger<McpReconnectionService> logger)
    {
        _mcpReconnectable = mcpReconnectable;
        _notificationSink = notificationSink;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let McpClientManager.StartAsync complete before polling.
        await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, stoppingToken);

        using var timer = new PeriodicTimer(TickInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckAndReconnectAsync(stoppingToken);
        }
    }

    internal async Task CheckAndReconnectAsync(CancellationToken ct)
    {
        var statuses = _mcpReconnectable.GetServerStatuses();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        foreach (var (serverName, status) in statuses)
        {
            if (status.State is not McpConnectionState.Unreachable)
            {
                _backoff.TryRemove(serverName, out _);
                continue;
            }

            var state = _backoff.GetValueOrDefault(serverName);
            var failureCount = state.FailureCount;
            var backoffMs = ComputeBackoffMs(failureCount);

            if (nowMs - state.LastAttemptMs < backoffMs)
                continue;

            _backoff[serverName] = state with { LastAttemptMs = nowMs };

            _logger.LogDebug(
                "Attempting reconnection to MCP server '{Name}' (attempt {Attempt})",
                serverName.Value, failureCount + 1);

            try
            {
                var success = await _mcpReconnectable.TryReconnectAsync(serverName, ct);
                if (success)
                {
                    _backoff.TryRemove(serverName, out _);

                    _logger.LogInformation(
                        "MCP server '{Name}' reconnected successfully after {Attempts} attempt(s)",
                        serverName.Value, failureCount + 1);

                    EmitReconnectedAlert(serverName);
                }
                else
                {
                    _backoff[serverName] = new BackoffState(failureCount + 1, nowMs);
                    _logger.LogDebug(
                        "MCP server '{Name}' reconnection failed (next retry in ~{BackoffSeconds}s)",
                        serverName.Value, ComputeBackoffMs(failureCount + 1) / 1000);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _backoff[serverName] = new BackoffState(failureCount + 1, nowMs);
                _logger.LogDebug(ex,
                    "MCP server '{Name}' reconnection threw an exception",
                    serverName.Value);
            }
        }
    }

    internal static long ComputeBackoffMs(int failureCount)
    {
        if (failureCount <= 0)
            return 0;

        var seconds = Math.Min(
            BaseBackoffSeconds * (1 << Math.Min(failureCount - 1, 10)),
            MaxBackoffSeconds);
        return seconds * 1000L;
    }

    private void EmitReconnectedAlert(McpServerName serverName)
    {
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "mcp.server.reconnected",
            AlertType.McpServerReconnected,
            $"MCP server '{serverName.Value}' reconnected successfully.",
            AlertSeverity.Info,
            source: serverName.Value,
            context: new Dictionary<string, string>
            {
                ["serverName"] = serverName.Value,
            }));
    }

    internal readonly record struct BackoffState(int FailureCount, long LastAttemptMs);
}
