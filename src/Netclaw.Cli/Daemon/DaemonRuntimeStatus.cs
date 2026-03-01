using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Wire types for deserializing the daemon runtime status endpoint response.
/// Nested types mirror the JSON shape returned by the daemon status API.
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
    }

    public sealed class Model : IWireType
    {
        public required string ModelId { get; init; }

        public required string Provider { get; init; }

        public required string InputModalities { get; init; }

        public required string OutputModalities { get; init; }

        public int ContextWindow { get; init; }
    }
}
