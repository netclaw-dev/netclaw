using System.Diagnostics;
using Microsoft.Extensions.Options;
using Netclaw.Actors.Memory;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
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
    SessionConfig sessionConfig,
    ModelSelection modelSelection,
    MemoryConfig memoryConfig,
    NetclawPaths paths,
    McpClientManager? mcpClientManager = null,
    FileMemoryStore? fileMemoryStore = null)
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
                OtlpEndpoint = telemetryOptions.Value.Otlp.Endpoint
            },
            Model = new DaemonRuntimeStatus.Model
            {
                ModelId = sessionConfig.ModelId,
                Provider = modelSelection.Main.Provider,
                InputModalities = sessionConfig.InputModalities.ToString(),
                OutputModalities = sessionConfig.OutputModalities.ToString(),
                ContextWindow = sessionConfig.ContextWindowTokens
            },
            Update = BuildUpdateStatus(),
            Memory = await BuildMemoryStatusAsync(cancellationToken)
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

    private static DaemonRuntimeStatus.Connector ToConnector(string name, McpServerStatus status)
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

    private static DaemonRuntimeStatus.Update? BuildUpdateStatus()
    {
        var result = UpdateCheckService.GetLastResult();
        if (result is null)
            return null;

        return new DaemonRuntimeStatus.Update
        {
            Available = result.IsUpdateAvailable,
            CurrentVersion = result.CurrentVersion,
            LatestVersion = result.IsUpdateAvailable ? result.LatestVersion : null,
            ReleaseNotesUrl = result.IsUpdateAvailable ? result.ReleaseNotesUrl : null,
        };
    }

    private async Task<DaemonRuntimeStatus.Memory> BuildMemoryStatusAsync(CancellationToken ct)
    {
        if (memoryConfig.Provider.Equals("memorizer", StringComparison.OrdinalIgnoreCase))
        {
            if (mcpClientManager is null)
            {
                return new DaemonRuntimeStatus.Memory
                {
                    Provider = "memorizer",
                    Status = "unavailable"
                };
            }

            var statuses = mcpClientManager.GetServerStatuses();
            if (statuses.TryGetValue("memorizer", out var memorizer))
            {
                return memorizer.State switch
                {
                    McpConnectionState.Connected => new DaemonRuntimeStatus.Memory
                    {
                        Provider = "memorizer",
                        Status = "healthy",
                        ToolCount = memorizer.ToolCount
                    },
                    _ => new DaemonRuntimeStatus.Memory
                    {
                        Provider = "memorizer",
                        Status = "degraded"
                    }
                };
            }

            return new DaemonRuntimeStatus.Memory
            {
                Provider = "memorizer",
                Status = "unavailable"
            };
        }

        // File backend
        if (fileMemoryStore is not null)
        {
            var entries = await fileMemoryStore.GetEntriesAsync(ct);
            return new DaemonRuntimeStatus.Memory
            {
                Provider = "files",
                Status = "healthy",
                MemoryCount = entries.Count,
                IndexPath = paths.MemoryIndexPath
            };
        }

        return new DaemonRuntimeStatus.Memory
        {
            Provider = "files",
            Status = "healthy",
            MemoryCount = 0,
            IndexPath = paths.MemoryIndexPath
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
