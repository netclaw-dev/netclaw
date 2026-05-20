// -----------------------------------------------------------------------
// <copyright file="NetclawResourceDetector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using OpenTelemetry.Resources;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Contributes default OpenTelemetry <c>service.*</c> resource attributes derived
/// from the running assembly and runtime environment, used when the standard
/// <c>OTEL_SERVICE_NAME</c> / <c>OTEL_RESOURCE_ATTRIBUTES</c> environment variables
/// do not supply them.
///
/// <para>
/// This detector MUST be registered before the environment-variable detector so
/// that env vars override these defaults — <see cref="ResourceBuilder.Build"/>
/// merges detectors in order and a later detector wins on key collision.
/// </para>
/// </summary>
public sealed class NetclawResourceDetector : IResourceDetector
{
    public Resource Detect() => new(
    [
        new KeyValuePair<string, object>("service.name", "netclawd"),
        new KeyValuePair<string, object>(
            "service.instance.id", $"{Environment.MachineName}:{Environment.ProcessId}"),
        new KeyValuePair<string, object>("service.version", BuildInfo.Version),
    ]);
}
