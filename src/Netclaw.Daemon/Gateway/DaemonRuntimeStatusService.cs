using System.Diagnostics;
using Microsoft.Extensions.Options;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Gateway;

public sealed class DaemonRuntimeStatusService(
    TimeProvider timeProvider,
    IEnumerable<IChannel> channels,
    SlackChannelOptions slackOptions,
    DaemonPersistenceOptions persistenceOptions,
    IOptions<TelemetryOptions> telemetryOptions)
{
    public async Task<DaemonRuntimeStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var now = timeProvider.GetUtcNow();
        var uptime = now - startedAt;

        var connectors = new List<ConnectorStatus>
        {
            await BuildSlackStatusAsync(cancellationToken)
        };

        var overall = ResolveOverallStatus(connectors);

        return new DaemonRuntimeStatusResponse
        {
            Overall = overall,
            Process = new ProcessStatus
            {
                Pid = process.Id,
                StartedAtUtc = startedAt,
                UptimeSeconds = (long)uptime.TotalSeconds
            },
            Connectors = connectors,
            Persistence = new PersistenceStatus
            {
                Provider = persistenceOptions.Provider.ToString()
            },
            Telemetry = new TelemetryStatus
            {
                Enabled = telemetryOptions.Value.Enabled,
                OtlpEndpoint = telemetryOptions.Value.Otlp.Endpoint
            }
        };
    }

    private async Task<ConnectorStatus> BuildSlackStatusAsync(CancellationToken cancellationToken)
    {
        if (!slackOptions.Enabled)
        {
            return new ConnectorStatus
            {
                Key = "slack",
                DisplayName = "Slack",
                Enabled = false,
                Status = "disabled",
                Message = "Slack connector is disabled in configuration."
            };
        }

        var slackChannel = channels.FirstOrDefault(c =>
            string.Equals(c.ChannelType, "slack", StringComparison.OrdinalIgnoreCase));

        if (slackChannel is null)
        {
            return new ConnectorStatus
            {
                Key = "slack",
                DisplayName = "Slack",
                Enabled = true,
                Status = "disconnected",
                Message = "Slack connector is enabled but was not registered."
            };
        }

        var health = await slackChannel.GetHealthAsync(cancellationToken);
        return new ConnectorStatus
        {
            Key = "slack",
            DisplayName = slackChannel.DisplayName,
            Enabled = true,
            Status = health.Status switch
            {
                ChannelHealthStatus.Healthy => "healthy",
                ChannelHealthStatus.Degraded => "degraded",
                ChannelHealthStatus.Disconnected => "disconnected",
                _ => "unknown"
            },
            Message = health.Detail
        };
    }

    private static string ResolveOverallStatus(IReadOnlyList<ConnectorStatus> connectors)
    {
        if (connectors.Any(c => c.Enabled && c.Status is "disconnected"))
            return "degraded";

        if (connectors.Any(c => c.Enabled && c.Status is "degraded"))
            return "degraded";

        return "healthy";
    }
}

public sealed class DaemonRuntimeStatusResponse
{
    public string Overall { get; init; } = "unknown";

    public required ProcessStatus Process { get; init; }

    public required List<ConnectorStatus> Connectors { get; init; }

    public required PersistenceStatus Persistence { get; init; }

    public required TelemetryStatus Telemetry { get; init; }
}

public sealed class ProcessStatus
{
    public int Pid { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    public long UptimeSeconds { get; init; }
}

public sealed class ConnectorStatus
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public bool Enabled { get; init; }

    public required string Status { get; init; }

    public string? Message { get; init; }
}

public sealed class PersistenceStatus
{
    public required string Provider { get; init; }
}

public sealed class TelemetryStatus
{
    public bool Enabled { get; init; }

    public string? OtlpEndpoint { get; init; }
}
