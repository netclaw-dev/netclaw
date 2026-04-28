// -----------------------------------------------------------------------
// <copyright file="DaemonRuntimeStatus.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Wire types for the daemon runtime status endpoint.
/// Nested types represent the JSON shape returned by the status API.
/// </summary>
public static class DaemonRuntimeStatus
{
    public sealed class Response : IWireType
    {
        public string Overall { get; init; } = "unknown";

        public required Build Build { get; init; }

        public required Process Process { get; init; }

        public required List<Connector> Connectors { get; init; }

        public required Persistence Persistence { get; init; }

        public required Telemetry Telemetry { get; init; }

        public Model? Model { get; init; }

        public Update? Update { get; init; }

        public Memory? Memory { get; init; }

        public Reminders? Reminders { get; init; }
    }

    public sealed class Build : IWireType
    {
        public required string Version { get; init; }

        public required string CommitHash { get; init; }

        public required string BuildTimestamp { get; init; }
    }

    public sealed class Process : IWireType
    {
        public int Pid { get; init; }

        public DateTimeOffset StartedAtUtc { get; init; }

        public long UptimeSeconds { get; init; }
    }

    public sealed class Connector : IWireType
    {
        public required string Key { get; init; }

        public required string DisplayName { get; init; }

        public bool Enabled { get; init; }

        public required string Status { get; init; }

        public string? Message { get; init; }
    }

    public sealed class Persistence : IWireType
    {
        public required string Provider { get; init; }
    }

    public sealed class Telemetry : IWireType
    {
        public bool Enabled { get; init; }

        public string? OtlpEndpoint { get; init; }

        public List<DaemonStats.ChannelActivity> Channels { get; init; } = [];
    }

    public sealed class Model : IWireType
    {
        public required string ModelId { get; init; }

        /// <summary>
        /// Human-friendly model name with file-format noise stripped
        /// (.gguf extension, quantization suffixes, Ollama tags).
        /// </summary>
        public string? DisplayName { get; init; }

        public required string Provider { get; init; }

        public required string InputModalities { get; init; }

        public required string OutputModalities { get; init; }

        public int ContextWindow { get; init; }
    }

    public sealed class Update : IWireType
    {
        public bool Available { get; init; }

        public bool SelfUpdateDisabled { get; init; }

        /// <summary>
        /// Update availability state: "up-to-date", "update-available", or "unknown" (check failed or not yet run).
        /// </summary>
        public string State { get; init; } = "unknown";

        public required string CurrentVersion { get; init; }

        public string? LatestVersion { get; init; }

        public string? ReleaseNotesUrl { get; init; }

        /// <summary>
        /// Diagnostic detail when <see cref="State"/> is "unknown" — e.g. the specific
        /// error that caused the check to fail.
        /// </summary>
        public string? ErrorDetail { get; init; }
    }

    public sealed class Memory : IWireType
    {
        public required string Provider { get; init; }

        public required string Status { get; init; }

        public string? DatabasePath { get; init; }

        public int? PendingCheckpoints { get; init; }
    }

    public sealed class Reminders : IWireType
    {
        /// <summary>Number of enabled reminder definitions currently scheduled.</summary>
        public int ScheduledCount { get; init; }

        /// <summary>Number of reminder executions currently in flight.</summary>
        public int ActiveExecutions { get; init; }

        /// <summary>Number of reminders that have recorded at least one consecutive failure.</summary>
        public int FailedCount { get; init; }
    }
}
