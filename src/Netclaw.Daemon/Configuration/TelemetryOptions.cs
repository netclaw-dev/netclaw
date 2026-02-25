namespace Netclaw.Daemon.Configuration;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public bool Enabled { get; init; }

    public TelemetryOtlpOptions Otlp { get; init; } = new();
}

public sealed class TelemetryOtlpOptions
{
    public string Endpoint { get; init; } = "http://127.0.0.1:4317";
}
