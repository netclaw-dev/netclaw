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
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Daemon.Gateway;

internal sealed class DaemonRuntimeStatusService(
    DaemonStartClock startClock,
    TimeProvider timeProvider,
    IChannelRegistry channelRegistry,
    IOptions<TelemetryOptions> telemetryOptions,
    ModelCapabilities modelCapabilities,
    ModelSelection modelSelection,
    DaemonConfig daemonConfig,
    NetclawPaths paths,
    IChatClientProvider chatClientProvider,
    ProviderRuntimeValidation providerValidation,
    McpClientManager? mcpClientManager = null,
    SQLiteMemoryStore? sqliteMemoryStore = null,
    MemoryEmbedderHolder? memoryEmbedderHolder = null,
    MemoryConfig? memoryConfig = null,
    IRequiredActor<ReminderManagerActorKey>? reminderManagerActor = null)
{
    public async Task<DaemonRuntimeStatus.Response> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        var now = timeProvider.GetUtcNow();
        var uptime = now - startClock.StartedAt;

        var connectors = await BuildChannelStatusesAsync(cancellationToken);

        connectors.AddRange(BuildMcpStatuses());

        var degraded = chatClientProvider.IsDegraded;
        var overall = ResolveOverallStatus(connectors, degraded);

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
                Provider = "Sqlite"
            },
            Telemetry = BuildTelemetry(),
            Model = new DaemonRuntimeStatus.Model
            {
                ModelId = degraded ? string.Empty : modelCapabilities.ModelId,
                DisplayName = degraded ? null : ModelIdNormalizer.GetDisplayName(modelCapabilities.ModelId),
                Provider = degraded ? string.Empty : modelSelection.Main.Provider,
                InputModalities = degraded ? string.Empty : modelCapabilities.InputModalities.ToString(),
                OutputModalities = degraded ? string.Empty : modelCapabilities.OutputModalities.ToString(),
                ContextWindow = degraded ? 0 : modelCapabilities.ContextWindowTokens,
                Degraded = degraded,
                DegradedReason = degraded ? providerValidation?.Reason : null,
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
        var databasePath = paths.SqliteDbPath;
        if (sqliteMemoryStore is null)
        {
            return new DaemonRuntimeStatus.Memory
            {
                Provider = "sqlite",
                Status = "unavailable",
                DatabasePath = databasePath
            };
        }

        try
        {
            var pending = await sqliteMemoryStore.GetPendingCheckpointCountAsync(ct);
            return new DaemonRuntimeStatus.Memory
            {
                Provider = "sqlite",
                Status = "healthy",
                DatabasePath = databasePath,
                PendingCheckpoints = pending,
                Embeddings = BuildEmbeddingsStatus()
            };
        }
        catch
        {
            return new DaemonRuntimeStatus.Memory
            {
                Provider = "sqlite",
                Status = "degraded",
                DatabasePath = databasePath,
                Embeddings = BuildEmbeddingsStatus()
            };
        }
    }

    /// <summary>
    /// Embeddings status (memory-core-redesign D2/Requirement "Loud degradation without silent
    /// fallback"): <c>"disabled"</c> when <c>Memory.Embeddings.Enabled</c> is false, <c>"ok"</c>
    /// when the resolved <see cref="IMemoryEmbedder"/> is available, otherwise <c>"degraded"</c>.
    /// </summary>
    private DaemonRuntimeStatus.Embeddings BuildEmbeddingsStatus()
    {
        if (memoryConfig?.Embeddings.Enabled != true)
        {
            return new DaemonRuntimeStatus.Embeddings { Status = "disabled" };
        }

        var embedder = memoryEmbedderHolder?.Current;
        if (embedder is { IsAvailable: true })
        {
            return new DaemonRuntimeStatus.Embeddings { Status = "ok", ModelId = embedder.ModelId };
        }

        return new DaemonRuntimeStatus.Embeddings
        {
            Status = "degraded",
            ModelId = embedder?.ModelId ?? memoryConfig.Embeddings.ModelId,
            DegradedReason = memoryEmbedderHolder is null
                ? "embedding subsystem not wired up"
                : "embedding model unavailable — see daemon logs for memory_embedding_unavailable"
        };
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

    internal static string ResolveOverallStatus(
        IReadOnlyList<DaemonRuntimeStatus.Connector> connectors,
        bool chatClientDegraded = false)
    {
        // A No-Op chat client means the daemon can't actually serve model
        // responses — surface that at the top level rather than reporting
        // "healthy" while every chat turn returns the configuration banner.
        if (chatClientDegraded)
            return "degraded";

        if (connectors.Any(c => c.Enabled && c.Status is "disconnected" or "auth-failed" or "auth-required"))
            return "degraded";

        if (connectors.Any(c => c.Enabled && c.Status is "degraded"))
            return "degraded";

        return "healthy";
    }
}
