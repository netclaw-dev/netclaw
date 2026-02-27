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
    public async Task<DaemonRuntimeStatus.Response> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var now = timeProvider.GetUtcNow();
        var uptime = now - startedAt;

        var connectors = new List<DaemonRuntimeStatus.Connector>
        {
            await BuildSlackStatusAsync(cancellationToken)
        };

        var overall = ResolveOverallStatus(connectors);

        return new DaemonRuntimeStatus.Response
        {
            Overall = overall,
            Process = new DaemonRuntimeStatus.Process
            {
                Pid = process.Id,
                StartedAtUtc = startedAt,
                UptimeSeconds = (long)uptime.TotalSeconds
            },
            Connectors = connectors,
            Persistence = new DaemonRuntimeStatus.Persistence
            {
                Provider = persistenceOptions.Provider.ToString()
            },
            Telemetry = new DaemonRuntimeStatus.Telemetry
            {
                Enabled = telemetryOptions.Value.Enabled,
                OtlpEndpoint = telemetryOptions.Value.Otlp.Endpoint
            }
        };
    }

    private async Task<DaemonRuntimeStatus.Connector> BuildSlackStatusAsync(CancellationToken cancellationToken)
    {
        if (!slackOptions.Enabled)
        {
            return new DaemonRuntimeStatus.Connector
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
            return new DaemonRuntimeStatus.Connector
            {
                Key = "slack",
                DisplayName = "Slack",
                Enabled = true,
                Status = "disconnected",
                Message = "Slack connector is enabled but was not registered."
            };
        }

        var health = await slackChannel.GetHealthAsync(cancellationToken);
        return new DaemonRuntimeStatus.Connector
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

    private static string ResolveOverallStatus(IReadOnlyList<DaemonRuntimeStatus.Connector> connectors)
    {
        if (connectors.Any(c => c.Enabled && c.Status is "disconnected"))
            return "degraded";

        if (connectors.Any(c => c.Enabled && c.Status is "degraded"))
            return "degraded";

        return "healthy";
    }
}
