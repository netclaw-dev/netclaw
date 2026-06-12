// -----------------------------------------------------------------------
// <copyright file="DaemonRuntimeStatusService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Options;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Daemon.Services;
using Netclaw.Tools;

namespace Netclaw.Daemon.Gateway;

internal sealed class DaemonRuntimeStatusService(
    DaemonStartClock startClock,
    TimeProvider timeProvider,
    IChannelRegistry channelRegistry,
    DaemonPersistenceOptions persistenceOptions,
    IOptions<TelemetryOptions> telemetryOptions,
    ModelCapabilities modelCapabilities,
    ModelSelection modelSelection,
    DaemonConfig daemonConfig,
    NetclawPaths paths,
    McpClientManager? mcpClientManager = null,
    SQLiteMemoryStore? sqliteMemoryStore = null,
    IRequiredActor<ReminderManagerActorKey>? reminderManagerActor = null)
{
    public async Task<DaemonRuntimeStatus.Response> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        var now = timeProvider.GetUtcNow();
        var uptime = now - startClock.StartedAt;

        var connectors = await BuildChannelStatusesAsync(cancellationToken);

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
                StartedAtUtc = startClock.StartedAt,
                UptimeSeconds = (long)uptime.TotalSeconds
            },
            Connectors = connectors,
            Persistence = new DaemonRuntimeStatus.Persistence
            {
                Provider = persistenceOptions.Provider.ToString()
            },
            Telemetry = BuildTelemetry(),
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

    private DaemonRuntimeStatus.Telemetry BuildTelemetry()
    {
        var enabledChannelTypes = channelRegistry.ListChannels()
            .Where(descriptor => descriptor.IsEnabled)
            .Select(descriptor => descriptor.ChannelType)
            .ToHashSet();

        var channelActivities = ChannelTelemetry.GetAllSnapshots()
            .Where(s => enabledChannelTypes.Contains(s.ChannelType))
            .Select(s => s.ToWireActivity())
            .ToList();

        return new DaemonRuntimeStatus.Telemetry
        {
            Enabled = telemetryOptions.Value.Enabled,
            OtlpEndpoint = telemetryOptions.Value.Otlp.Endpoint,
            Channels = channelActivities
        };
    }

    private async Task<List<DaemonRuntimeStatus.Connector>> BuildChannelStatusesAsync(
        CancellationToken cancellationToken)
    {
        var connectors = new List<DaemonRuntimeStatus.Connector>();

        foreach (var descriptor in channelRegistry.ListChannels())
        {
            var snapshot = await channelRegistry.GetSnapshotAsync(descriptor.Key, cancellationToken);
            connectors.Add(ToConnector(descriptor, snapshot));
        }

        return connectors;
    }

    internal static DaemonRuntimeStatus.Connector ToConnector(
        ChannelDescriptor descriptor,
        ChannelRuntimeSnapshot snapshot)
    {
        return new DaemonRuntimeStatus.Connector
        {
            Key = descriptor.Key.Value,
            DisplayName = descriptor.DisplayName,
            Enabled = snapshot.IsEnabled,
            Status = snapshot.IsEnabled
                ? snapshot.Health switch
                {
                    ChannelHealthStatus.Healthy => "healthy",
                    ChannelHealthStatus.Degraded => "degraded",
                    ChannelHealthStatus.Disconnected => "disconnected",
                    _ => "unknown"
                }
                : "disabled",
            Message = snapshot.HealthDetail
        };
    }

    private IReadOnlyList<DaemonRuntimeStatus.Connector> BuildMcpStatuses()
    {
        if (mcpClientManager is null)
            return [];

        return mcpClientManager
            .GetServerStatuses()
            .OrderBy(x => x.Key.Value, StringComparer.OrdinalIgnoreCase)
            .Select(x => ToConnector(x.Key, x.Value))
            .ToList();
    }

    internal static DaemonRuntimeStatus.Connector ToConnector(McpServerName name, McpServerStatus status)
    {
        var key = $"mcp:{name.Value}";
        var displayName = $"MCP/{name.Value}";

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

    private DaemonRuntimeStatus.Update BuildUpdateStatus()
    {
        var result = UpdateCheckService.GetLastResult();
        if (result is null)
        {
            return new DaemonRuntimeStatus.Update
            {
                Available = false,
                State = "unknown",
                // FullVersion to match the post-check branch (result.CurrentVersion is
                // the full version), so a beta build doesn't show a stripped core here.
                CurrentVersion = BuildInfo.FullVersion,
                SelfUpdateDisabled = daemonConfig.DisableSelfUpdate,
            };
        }

        var state = result switch
        {
            { CheckSucceeded: false } => "unknown",
            { IsUpdateAvailable: true } => "update-available",
            _ => "up-to-date",
        };

        return new DaemonRuntimeStatus.Update
        {
            Available = result.IsUpdateAvailable,
            SelfUpdateDisabled = daemonConfig.DisableSelfUpdate,
            State = state,
            CurrentVersion = result.CurrentVersion,
            LatestVersion = result.IsUpdateAvailable ? result.LatestVersion : null,
            ReleaseNotesUrl = result.IsUpdateAvailable ? result.ReleaseNotesUrl : null,
            ErrorDetail = result.CheckSucceeded ? null : result.ErrorDetail,
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
