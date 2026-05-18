// -----------------------------------------------------------------------
// <copyright file="TelemetryOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Daemon.Configuration;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public bool Enabled { get; init; }

    /// <summary>
    /// OpenTelemetry <c>service.name</c> reported by this netclaw instance. When
    /// unset, falls back to the standard <c>OTEL_SERVICE_NAME</c> environment
    /// variable, then to <c>"netclawd"</c>. Set this to tell otherwise-identical
    /// netclaw instances apart (e.g. per-agent) in a shared observability backend.
    /// </summary>
    public string? ServiceName { get; init; }

    public TelemetryOtlpOptions Otlp { get; init; } = new();
}

public sealed class TelemetryOtlpOptions
{
    public string Endpoint { get; init; } = "http://127.0.0.1:4317";
}
