// -----------------------------------------------------------------------
// <copyright file="ExposureMode.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

    /// <summary>Daemon is reachable behind a trusted reverse proxy.</summary>
    ReverseProxy,

    /// <summary>Daemon is reachable via Tailscale Serve (same tailnet only).</summary>
    TailscaleServe,

    /// <summary>Daemon is reachable via Tailscale Funnel (public internet).</summary>
    TailscaleFunnel,

    /// <summary>Daemon is reachable via Cloudflare Tunnel.</summary>
    CloudflareTunnel
}

/// <summary>
/// Extension methods for <see cref="ExposureMode"/>.
/// </summary>
public static class ExposureModeExtensions
{
    /// <summary>Returns the kebab-case wire value expected by the JSON schema.</summary>
    public static string ToWireValue(this ExposureMode mode) => mode switch
    {
        ExposureMode.Local => "local",
        ExposureMode.ReverseProxy => "reverse-proxy",
        ExposureMode.TailscaleServe => "tailscale-serve",
        ExposureMode.TailscaleFunnel => "tailscale-funnel",
        ExposureMode.CloudflareTunnel => "cloudflare-tunnel",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Unknown ExposureMode value: {mode}")
    };

    /// <summary>
    /// Returns the OS process name required by the given tunnel exposure mode,
    /// or <c>null</c> for <see cref="ExposureMode.Local"/> (no tunnel needed).
    /// </summary>
    public static string? GetRequiredProcessName(this ExposureMode mode) => mode switch
    {
        ExposureMode.Local => null,
        ExposureMode.ReverseProxy => null,
        ExposureMode.TailscaleServe or ExposureMode.TailscaleFunnel => "tailscaled",
        ExposureMode.CloudflareTunnel => "cloudflared",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Unknown ExposureMode value: {mode}")
    };

    /// <summary>
    /// Returns <c>true</c> when the mode accepts remote traffic and therefore requires
    /// an explicit remote authentication path.
    /// </summary>
    public static bool RequiresRemoteAuthentication(this ExposureMode mode) => mode switch
    {
        ExposureMode.Local => false,
        ExposureMode.ReverseProxy or ExposureMode.TailscaleServe or ExposureMode.TailscaleFunnel or ExposureMode.CloudflareTunnel => true,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Unknown ExposureMode value: {mode}")
    };

    /// <summary>
    /// Returns <c>true</c> for the three tunnel modes (TailscaleServe / TailscaleFunnel /
    /// CloudflareTunnel), where the local tunnel agent forwards remote traffic over loopback
    /// and the daemon cannot distinguish a same-host caller from a remote one at the socket
    /// level. ReverseProxy is excluded — it has its own auth topology (forwarded headers +
    /// non-loopback bind + TrustedProxies). Local is excluded — no tunnel involved.
    /// </summary>
    public static bool IsTunnelMode(this ExposureMode mode) => mode switch
    {
        ExposureMode.Local or ExposureMode.ReverseProxy => false,
        ExposureMode.TailscaleServe or ExposureMode.TailscaleFunnel or ExposureMode.CloudflareTunnel => true,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Unknown ExposureMode value: {mode}")
    };
}

/// <summary>
/// JSON converter for <see cref="ExposureMode"/> that serializes to/from
/// lowercase kebab-case wire values (e.g., <c>tailscale-serve</c>).
/// </summary>
internal sealed class ExposureModeJsonConverter()
    : JsonStringEnumConverter<ExposureMode>(JsonNamingPolicy.KebabCaseLower);
