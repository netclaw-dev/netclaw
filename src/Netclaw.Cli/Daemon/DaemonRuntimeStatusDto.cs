namespace Netclaw.Cli.Daemon;

public sealed class DaemonRuntimeStatusDto
{
    public string Overall { get; init; } = "unknown";

    public required DaemonProcessStatusDto Process { get; init; }

    public required List<DaemonConnectorStatusDto> Connectors { get; init; }

    public required DaemonPersistenceStatusDto Persistence { get; init; }

    public required DaemonTelemetryStatusDto Telemetry { get; init; }
}

public sealed class DaemonProcessStatusDto
{
    public int Pid { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    public long UptimeSeconds { get; init; }
}

public sealed class DaemonConnectorStatusDto
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public bool Enabled { get; init; }

    public required string Status { get; init; }

    public string? Message { get; init; }
}

public sealed class DaemonPersistenceStatusDto
{
    public required string Provider { get; init; }
}

public sealed class DaemonTelemetryStatusDto
{
    public bool Enabled { get; init; }

    public string? OtlpEndpoint { get; init; }
}
