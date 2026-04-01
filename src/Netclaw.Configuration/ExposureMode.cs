using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// How the daemon is exposed to the network. Determines which tunnel prerequisite
/// checks run at startup and which doctor checks apply.
/// </summary>
[JsonConverter(typeof(ExposureModeJsonConverter))]
public enum ExposureMode
{
    /// <summary>Daemon binds loopback only. No tunnel required.</summary>
    Local,

    /// <summary>Daemon is reachable via Tailscale Serve (same tailnet only).</summary>
    TailscaleServe,

    /// <summary>Daemon is reachable via Tailscale Funnel (public internet).</summary>
    TailscaleFunnel,

    /// <summary>Daemon is reachable via Cloudflare Tunnel.</summary>
    CloudflareTunnel
}

/// <summary>
/// JSON converter for <see cref="ExposureMode"/> that serializes to/from
/// lowercase kebab-case wire values (e.g., <c>tailscale-serve</c>).
/// </summary>
internal sealed class ExposureModeJsonConverter()
    : JsonStringEnumConverter<ExposureMode>(JsonNamingPolicy.KebabCaseLower);
