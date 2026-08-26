// -----------------------------------------------------------------------
// <copyright file="TelemetryDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class TelemetryDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, readError) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (readError is not null)
            return Task.FromResult(DoctorCheckResult.Pass("Telemetry", "Skipped (base config is missing or invalid)."));

        if (root!["Telemetry"] is not JsonObject telemetry || !DoctorJsonConfigReader.ReadBool(telemetry, "Enabled"))
            return Task.FromResult(DoctorCheckResult.Pass("Telemetry", "Telemetry disabled or not configured."));

        var endpoint = telemetry["Otlp"]?["Endpoint"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Telemetry",
                "Telemetry is enabled without Telemetry:Otlp:Endpoint; default endpoint will be used.",
                "Set `Telemetry:Otlp:Endpoint` (e.g. http://127.0.0.1:4317)."));
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Telemetry",
                "Telemetry:Otlp:Endpoint must be an absolute URI.",
                "Set `Telemetry:Otlp:Endpoint` to a valid absolute URI."));
        }

        return Task.FromResult(DoctorCheckResult.Pass("Telemetry", "Telemetry endpoint is valid."));
    }
}
