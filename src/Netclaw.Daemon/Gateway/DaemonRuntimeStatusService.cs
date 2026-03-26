using System.Diagnostics;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Options;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Mcp;

namespace Netclaw.Daemon.Gateway;

internal sealed class DaemonRuntimeStatusService(
    TimeProvider timeProvider,
    IEnumerable<IChannel> channels,
    SlackChannelOptions slackOptions,
    DaemonPersistenceOptions persistenceOptions,
    IOptions<TelemetryOptions> telemetryOptions,
    ModelCapabilities modelCapabilities,
    ModelSelection modelSelection,
    NetclawPaths paths,
    McpClientManager? mcpClientManager = null,
    SQLiteMemoryStore? sqliteMemoryStore = null,
    IRequiredActor<ReminderManagerActorKey>? reminderManagerActor = null)
{
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();

    public async Task<DaemonRuntimeStatus.Response> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        var now = timeProvider.GetUtcNow();
        var uptime = now - _startedAt;

        var connectors = new List<DaemonRuntimeStatus.Connector>
        {
            await BuildSlackStatusAsync(cancellationToken)
        };

        connectors.AddRange(BuildMcpStatuses());

        var overall = ResolveOverallStatus(connectors);

        return new DaemonRuntimeStatus.Response
        {
            Overall = overall,
            Build = new DaemonRuntimeStatus.Build
            {
                Version = BuildInfo.Version,
                CommitHash = BuildInfo.CommitHash,
                BuildTimestamp = BuildInfo.BuildTimestamp
            },
            Process = new DaemonRuntimeStatus.Process
            {
                Pid = process.Id,
                StartedAtUtc = _startedAt,
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
                OtlpEndpoint = telemetryOptions.Value.Otlp.Endpoint,
                SlackCounters = BuildSlackCounters()
            },
            Model = new DaemonRuntimeStatus.Model
            {
                ModelId = modelCapabilities.ModelId,
                DisplayName = ModelIdNormalizer.GetDisplayName(modelCapabilities.ModelId),
                Provider = modelSelection.Main.Provider,
                InputModalities = modelCapabilities.InputModalities.ToString(),
                OutputModalities = modelCapabilities.OutputModalities.ToString(),
                ContextWindow = modelCapabilities.ContextWindowTokens
            },
            Update = BuildUpdateStatus(),
            Memory = await BuildMemoryStatusAsync(cancellationToken),
            Reminders = await BuildReminderHealthAsync(cancellationToken)
        };
    }

    private static DaemonRuntimeStatus.SlackCounters BuildSlackCounters()
    {
        var snapshot = ChannelTelemetry.GetSnapshot();
        return new DaemonRuntimeStatus.SlackCounters
        {
            EventsReceived = snapshot.SlackEventsReceived,
            EventsDropped = snapshot.SlackEventsDropped,
            EventsRouted = snapshot.SlackEventsRouted,
            MessagesEnqueued = snapshot.SlackMessagesEnqueued,
            RepliesPosted = snapshot.SlackRepliesPosted,
            RepliesRejected = snapshot.SlackRepliesRejected,
            RepliesFailed = snapshot.SlackRepliesFailed
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
            c.ChannelType == Actors.Channels.ChannelType.Slack);

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

    private IReadOnlyList<DaemonRuntimeStatus.Connector> BuildMcpStatuses()
    {
        if (mcpClientManager is null)
            return [];

        return mcpClientManager
            .GetServerStatuses()
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => ToConnector(x.Key, x.Value))
            .ToList();
    }

    internal static DaemonRuntimeStatus.Connector ToConnector(string name, McpServerStatus status)
    {
        var key = $"mcp:{name}";
        var displayName = $"MCP/{name}";

        return status.State switch
        {
            McpConnectionState.Disabled => new DaemonRuntimeStatus.Connector
            {
                Key = key,
                DisplayName = displayName,
                Enabled = false,
                Status = "disabled",
                Message = "MCP server is disabled in configuration."
            },

            McpConnectionState.Connected when status.ToolCount > 0 => new DaemonRuntimeStatus.Connector
            {
                Key = key,
                DisplayName = displayName,
                Enabled = true,
                Status = "healthy",
                Message = $"Connected ({status.ToolCount} tools discovered)."
            },

            McpConnectionState.Connected => new DaemonRuntimeStatus.Connector
            {
                Key = key,
                DisplayName = displayName,
                Enabled = true,
                Status = "degraded",
                Message = "Connected but no tools were discovered."
            },

            McpConnectionState.AwaitingAuth => new DaemonRuntimeStatus.Connector
            {
                Key = key,
                DisplayName = displayName,
                Enabled = true,
                Status = "auth-required",
                Message = string.IsNullOrWhiteSpace(status.ErrorMessage)
                    ? "OAuth authorization is required."
                    : status.ErrorMessage
            },

            McpConnectionState.AuthFailed => new DaemonRuntimeStatus.Connector
            {
                Key = key,
                DisplayName = displayName,
                Enabled = true,
                Status = "auth-failed",
                Message = string.IsNullOrWhiteSpace(status.ErrorMessage)
                    ? "Authentication to the MCP server failed."
                    : status.ErrorMessage
            },

            McpConnectionState.Unreachable => new DaemonRuntimeStatus.Connector
            {
                Key = key,
                DisplayName = displayName,
                Enabled = true,
                Status = "disconnected",
                Message = string.IsNullOrWhiteSpace(status.ErrorMessage)
                    ? "Failed to reach MCP server."
                    : status.ErrorMessage
            },

            _ => new DaemonRuntimeStatus.Connector
            {
                Key = key,
                DisplayName = displayName,
                Enabled = true,
                Status = "disconnected",
                Message = string.IsNullOrWhiteSpace(status.ErrorMessage)
                    ? "Failed to connect to MCP server."
                    : status.ErrorMessage
            }
        };
    }

    private static DaemonRuntimeStatus.Update BuildUpdateStatus()
    {
        var result = UpdateCheckService.GetLastResult();
        if (result is null)
        {
            return new DaemonRuntimeStatus.Update
            {
                Available = false,
                State = "unknown",
                CurrentVersion = BuildInfo.Version,
            };
        }

        return new DaemonRuntimeStatus.Update
        {
            Available = result.IsUpdateAvailable,
            State = result.IsUpdateAvailable ? "update-available" : "up-to-date",
            CurrentVersion = result.CurrentVersion,
            LatestVersion = result.IsUpdateAvailable ? result.LatestVersion : null,
            ReleaseNotesUrl = result.IsUpdateAvailable ? result.ReleaseNotesUrl : null,
        };
    }

    private async Task<DaemonRuntimeStatus.Memory> BuildMemoryStatusAsync(CancellationToken ct)
    {
        if (sqliteMemoryStore is null)
        {
            return new DaemonRuntimeStatus.Memory
            {
                Provider = "sqlite",
                Status = "unavailable",
                DatabasePath = paths.MemorySqliteDbPath
            };
        }

        try
        {
            var pending = await sqliteMemoryStore.GetPendingCheckpointCountAsync(ct);
            return new DaemonRuntimeStatus.Memory
            {
                Provider = "sqlite",
                Status = "healthy",
                DatabasePath = paths.MemorySqliteDbPath,
                PendingCheckpoints = pending
            };
        }
        catch
        {
            return new DaemonRuntimeStatus.Memory
            {
                Provider = "sqlite",
                Status = "degraded",
                DatabasePath = paths.MemorySqliteDbPath
            };
        }
    }

    private async Task<DaemonRuntimeStatus.Reminders?> BuildReminderHealthAsync(CancellationToken ct)
    {
        if (reminderManagerActor is null)
            return null;

        try
        {
            var actorRef = await reminderManagerActor.GetAsync(ct);
            var response = await actorRef.Ask<ReminderHealthResponse>(
                GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(3), ct);
            return new DaemonRuntimeStatus.Reminders
            {
                ScheduledCount = response.ScheduledCount,
                ActiveExecutions = response.ActiveExecutions,
                FailedCount = response.FailedCount
            };
        }
        catch
        {
            return null;
        }
    }

    internal static string ResolveOverallStatus(IReadOnlyList<DaemonRuntimeStatus.Connector> connectors)
    {
        if (connectors.Any(c => c.Enabled && c.Status is "disconnected" or "auth-failed" or "auth-required"))
            return "degraded";

        if (connectors.Any(c => c.Enabled && c.Status is "degraded"))
            return "degraded";

        return "healthy";
    }
}
