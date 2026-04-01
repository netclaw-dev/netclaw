using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>
/// Operator-facing daemon network configuration. Bound from the <c>Daemon</c>
/// section in <c>netclaw.json</c> at startup.
/// </summary>
public sealed record DaemonConfig
{
    /// <summary>
    /// IP address the daemon binds to. Defaults to loopback (<c>127.0.0.1</c>).
    /// </summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>
    /// TCP port the daemon listens on. Defaults to <c>5199</c>.
    /// </summary>
    public int Port { get; init; } = 5199;

    /// <summary>
    /// Declares which tunnel infrastructure (if any) is in front of the daemon.
    /// Used for startup prerequisite validation and doctor checks.
    /// Defaults to <see cref="ExposureMode.Local"/> (loopback only, no tunnel).
    /// </summary>
    public ExposureMode ExposureMode { get; init; } = ExposureMode.Local;

    /// <summary>
    /// Bind <see cref="DaemonConfig"/> from an <see cref="IConfigurationSection"/>.
    /// Handles kebab-case <c>ExposureMode</c> values (e.g., <c>tailscale-serve</c>).
    /// Returns defaults when the section is missing or empty.
    /// </summary>
    public static DaemonConfig BindFromConfiguration(IConfigurationSection? section)
    {
        if (section is null || !section.Exists())
            return new DaemonConfig();

        var host = section["Host"] ?? "127.0.0.1";
        var port = section.GetValue<int?>("Port") ?? 5199;
        var modeStr = section["ExposureMode"];
        var mode = ParseExposureMode(modeStr);

        return new DaemonConfig { Host = host, Port = port, ExposureMode = mode };
    }

    /// <summary>
    /// Parses an <see cref="ExposureMode"/> from a config string value.
    /// Accepts kebab-case (<c>tailscale-serve</c>) and PascalCase (<c>TailscaleServe</c>).
    /// Returns <see cref="ExposureMode.Local"/> for null/empty input.
    /// </summary>
    public static ExposureMode ParseExposureMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ExposureMode.Local;

        return value.Trim().ToLowerInvariant() switch
        {
            "local" => ExposureMode.Local,
            "tailscale-serve" or "tailscaleserve" => ExposureMode.TailscaleServe,
            "tailscale-funnel" or "tailscalefunnel" => ExposureMode.TailscaleFunnel,
            "cloudflare-tunnel" or "cloudflaretunnel" => ExposureMode.CloudflareTunnel,
            _ => throw new InvalidOperationException(
                $"Unknown ExposureMode value: '{value.Trim()}'. " +
                "Valid values: local, tailscale-serve, tailscale-funnel, cloudflare-tunnel.")
        };
    }
}
