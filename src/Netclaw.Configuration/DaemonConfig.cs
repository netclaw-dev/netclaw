// -----------------------------------------------------------------------
// <copyright file="DaemonConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using System.Net;

namespace Netclaw.Configuration;

/// <summary>
/// Operator-facing daemon network configuration. Bound from the <c>Daemon</c>
/// section in <c>netclaw.json</c> at startup.
/// </summary>
public sealed record DaemonConfig
{
    /// <summary>
    /// Default TCP port the daemon listens on when <c>Daemon.Port</c> is not set
    /// in <c>netclaw.json</c>. Single source of truth for the port literal.
    /// </summary>
    public const int DefaultPort = 5199;

    /// <summary>
    /// IP address the daemon binds to. Defaults to loopback (<c>127.0.0.1</c>).
    /// </summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>
    /// TCP port the daemon listens on. Defaults to <see cref="DefaultPort"/>.
    /// </summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// Declares which tunnel infrastructure (if any) is in front of the daemon.
    /// Used for startup prerequisite validation and doctor checks.
    /// Defaults to <see cref="ExposureMode.Local"/> (loopback only, no tunnel).
    /// </summary>
    public ExposureMode ExposureMode { get; init; } = ExposureMode.Local;

    /// <summary>
    /// When true, in-place binary self-update via <c>netclaw update</c> is blocked.
    /// Update availability checks still run so operators see when a new version exists.
    /// Intended for container deployments where the image tag IS the version.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool DisableSelfUpdate { get; init; }

    /// <summary>
    /// Release channel the update check follows. <see cref="Netclaw.Configuration.UpdateChannel.Stable"/>
    /// (default) only ever sees stable releases; <see cref="Netclaw.Configuration.UpdateChannel.Beta"/>
    /// opts into prereleases (and rolls onto a stable release once it supersedes a beta).
    /// Stable clients are never offered a prerelease.
    /// </summary>
    public UpdateChannel UpdateChannel { get; init; } = UpdateChannel.Stable;

    /// <summary>
    /// Explicitly trusted reverse-proxy source addresses or CIDR ranges. Only used when
    /// <see cref="ExposureMode"/> is <see cref="Netclaw.Configuration.ExposureMode.ReverseProxy"/>.
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    /// <summary>
    /// When true, skips the default local tunnel-process prerequisite check for tunnel-backed
    /// exposure modes. Intended only for sidecar or host-managed tunnel topologies.
    /// </summary>
    public bool SkipTunnelProcessCheck { get; init; }

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
        var port = section.GetValue<int?>("Port") ?? DefaultPort;
        var modeStr = section["ExposureMode"];
        var mode = ParseExposureMode(modeStr);
        var disableSelfUpdate = section.GetValue<bool?>("DisableSelfUpdate") ?? false;
        var updateChannel = ParseUpdateChannel(section["UpdateChannel"]);
        var trustedProxies = section.GetSection("TrustedProxies").Get<string[]>() ?? [];
        var skipTunnelProcessCheck = section.GetValue<bool?>("SkipTunnelProcessCheck") ?? false;

        return new DaemonConfig
        {
            Host = host,
            Port = port,
            ExposureMode = mode,
            DisableSelfUpdate = disableSelfUpdate,
            UpdateChannel = updateChannel,
            TrustedProxies = trustedProxies,
            SkipTunnelProcessCheck = skipTunnelProcessCheck
        };
    }

    /// <summary>
    /// Parses an <see cref="UpdateChannel"/> from a config string value.
    /// Returns <see cref="UpdateChannel.Stable"/> for null/empty input; throws on an
    /// unknown value rather than silently defaulting (a typo'd channel should fail loudly).
    /// </summary>
    public static UpdateChannel ParseUpdateChannel(string? value)
    {
        // Config binding treats null/empty as the default; only a non-empty,
        // unrecognized value is a typo worth failing on.
        if (string.IsNullOrWhiteSpace(value))
            return UpdateChannel.Stable;

        if (TryParseUpdateChannel(value, out var channel))
            return channel;

        throw new InvalidOperationException(
            $"Unknown UpdateChannel value: '{value.Trim()}'. Valid values: stable, beta.");
    }

    /// <summary>
    /// Tries to parse an <see cref="UpdateChannel"/> from a string. Returns false for
    /// null, whitespace, or any value other than <c>stable</c>/<c>beta</c> so callers
    /// (e.g. CLI argument parsing) can fail loudly instead of silently defaulting.
    /// Inverse of <see cref="UpdateChannelExtensions.ToWireValue"/>.
    /// </summary>
    public static bool TryParseUpdateChannel(string? value, out UpdateChannel channel)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "stable":
                channel = UpdateChannel.Stable;
                return true;
            case "beta":
                channel = UpdateChannel.Beta;
                return true;
            default:
                channel = UpdateChannel.Stable;
                return false;
        }
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
            "reverse-proxy" or "reverseproxy" => ExposureMode.ReverseProxy,
            "tailscale-serve" or "tailscaleserve" => ExposureMode.TailscaleServe,
            "tailscale-funnel" or "tailscalefunnel" => ExposureMode.TailscaleFunnel,
            "cloudflare-tunnel" or "cloudflaretunnel" => ExposureMode.CloudflareTunnel,
            _ => throw new InvalidOperationException(
                $"Unknown ExposureMode value: '{value.Trim()}'. " +
                "Valid values: local, reverse-proxy, tailscale-serve, tailscale-funnel, cloudflare-tunnel.")
        };
    }
}

/// <summary>
/// Shared daemon exposure validation and trusted-proxy parsing helpers used by startup,
/// doctor, and reverse-proxy request handling.
/// </summary>
public static class DaemonExposureValidator
{
    public static IReadOnlyList<DaemonExposureValidationIssue> Validate(
        DaemonConfig config,
        bool hasRemoteAuthenticationPath)
    {
        var issues = new List<DaemonExposureValidationIssue>();

        if (TryGetInvalidTrustedProxy(config.TrustedProxies, out var invalidTrustedProxyError))
        {
            issues.Add(new DaemonExposureValidationIssue(
                invalidTrustedProxyError!,
                "Each Daemon.TrustedProxies entry must be a literal IP address or CIDR, for example '10.0.0.5' or '10.0.0.0/24'.",
                IsTrustedProxyIssue: true));
        }

        if (GetLoopbackViolationIssue(config) is { } loopbackIssue)
            issues.Add(loopbackIssue);

        if (!config.ExposureMode.RequiresRemoteAuthentication())
            return issues;

        if (!hasRemoteAuthenticationPath)
        {
            issues.Add(new DaemonExposureValidationIssue(
                $"No remote authentication available: ExposureMode='{config.ExposureMode.ToWireValue()}' requires either at least one paired device or an alternative remote auth scheme.",
                "Run 'netclaw daemon pair' to pair a device, or check your auth configuration before exposing the daemon remotely."));
        }

        if (config.ExposureMode == ExposureMode.ReverseProxy)
        {
            if (IsLoopbackHost(config.Host))
            {
                issues.Add(new DaemonExposureValidationIssue(
                    $"Invalid reverse-proxy topology: Daemon.Host '{config.Host}' is loopback. Loopback auto-auth is reserved for true local operator traffic and cannot be inherited through a reverse proxy.",
                    "Bind the daemon to a non-loopback internal IP, such as 10.x.x.x or 192.168.x.x, and have the proxy forward to that address instead of 127.0.0.1, ::1, or localhost."));
            }

            if (config.TrustedProxies.Count == 0)
            {
                issues.Add(new DaemonExposureValidationIssue(
                    "Invalid reverse-proxy topology: Daemon.TrustedProxies must contain at least one explicitly trusted proxy address or CIDR.",
                    "Add the reverse proxy's source IP or subnet to Daemon.TrustedProxies so forwarded headers are only honored from known peers."));
            }
        }

        return issues;
    }

    public static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var trimmed = host.Trim();
        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(trimmed, out var address) && IPAddress.IsLoopback(address);
    }

    public static bool TryParseTrustedProxy(string rawValue, out ParsedTrustedProxy? parsedProxy, out string? error)
    {
        parsedProxy = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            error = "Invalid trusted proxy entry ''. Value cannot be empty.";
            return false;
        }

        var trimmed = rawValue.Trim();
        var slashIndex = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex < 0)
        {
            if (!IPAddress.TryParse(trimmed, out var address))
            {
                error = $"Invalid trusted proxy entry '{trimmed}'. Value is not a valid IP address or CIDR.";
                return false;
            }

            parsedProxy = new ParsedTrustedProxy(trimmed, address, PrefixLength: null);
            return true;
        }

        var addressPart = trimmed[..slashIndex];
        var prefixPart = trimmed[(slashIndex + 1)..];
        if (!IPAddress.TryParse(addressPart, out var networkAddress))
        {
            error = $"Invalid trusted proxy entry '{trimmed}'. '{addressPart}' is not a valid IP address.";
            return false;
        }

        if (!int.TryParse(prefixPart, out var prefixLength))
        {
            error = $"Invalid trusted proxy entry '{trimmed}'. '{prefixPart}' is not a valid CIDR prefix length.";
            return false;
        }

        var maxPrefix = networkAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefix)
        {
            error = $"Invalid trusted proxy entry '{trimmed}'. CIDR prefix length must be between 0 and {maxPrefix}.";
            return false;
        }

        parsedProxy = new ParsedTrustedProxy(trimmed, networkAddress, prefixLength);
        return true;
    }

    /// <summary>
    /// Parses all trusted proxy entries. Throws on invalid entries — callers must validate
    /// with <see cref="TryGetInvalidTrustedProxy"/> before calling this method.
    /// </summary>
    public static IReadOnlyList<ParsedTrustedProxy> ParseTrustedProxies(IEnumerable<string> trustedProxies)
    {
        var parsed = new List<ParsedTrustedProxy>();
        foreach (var trustedProxy in trustedProxies)
        {
            if (!TryParseTrustedProxy(trustedProxy, out var entry, out var error))
                throw new InvalidOperationException(error);

            parsed.Add(entry!);
        }

        return parsed;
    }

    public static bool TryGetInvalidTrustedProxy(IEnumerable<string> trustedProxies, out string? error)
    {
        foreach (var trustedProxy in trustedProxies)
        {
            if (!TryParseTrustedProxy(trustedProxy, out _, out error))
                return true;
        }

        error = null;
        return false;
    }

    public static DaemonExposureValidationIssue? GetLoopbackViolationIssue(DaemonConfig config)
    {
        if (config.ExposureMode != ExposureMode.Local || IsLoopbackHost(config.Host))
            return null;

        return new DaemonExposureValidationIssue(
            $"Invalid local topology: Daemon.Host '{config.Host}' is not a loopback address. ExposureMode 'local' requires binding to 127.0.0.1, ::1, or localhost.",
            "Either bind to a loopback address, or set Daemon.ExposureMode to the appropriate tunnel or reverse-proxy mode for non-loopback traffic.");
    }

    public static DaemonExposureValidationIssue? GetMissingRequiredProcessIssue(
        DaemonConfig config,
        Func<string, bool> processDetector)
    {
        ArgumentNullException.ThrowIfNull(processDetector);

        var requiredProcess = config.ExposureMode.GetRequiredProcessName();
        if (requiredProcess is null || config.SkipTunnelProcessCheck || processDetector(requiredProcess))
            return null;

        return new DaemonExposureValidationIssue(
            $"Tunnel prerequisite not met: ExposureMode='{config.ExposureMode.ToWireValue()}' requires '{requiredProcess}' to be running unless Daemon.SkipTunnelProcessCheck=true is explicitly set for a sidecar or host-managed tunnel topology.",
            $"Start '{requiredProcess}' locally, or set Daemon.SkipTunnelProcessCheck=true only when the tunnel is intentionally managed outside the Netclaw process namespace.");
    }
}

public sealed record DaemonExposureValidationIssue(string Message, string Remediation, bool IsTrustedProxyIssue = false);

public sealed record ParsedTrustedProxy(string RawValue, IPAddress Address, int? PrefixLength);

/// <summary>
/// Release channel the update check follows. Bound from <c>Daemon.UpdateChannel</c>.
/// </summary>
public enum UpdateChannel
{
    /// <summary>Stable releases only (default). Never offered a prerelease.</summary>
    Stable,

    /// <summary>
    /// Opt into prereleases. Resolves to the newest of {stable, prerelease}, so a
    /// stable release that supersedes a beta is still offered.
    /// </summary>
    Beta,
}

/// <summary>
/// Extension helpers for <see cref="UpdateChannel"/>.
/// </summary>
public static class UpdateChannelExtensions
{
    /// <summary>
    /// Returns the lowercase wire value expected by the JSON schema. Defined as an
    /// explicit mapping rather than <c>ToString().ToLowerInvariant()</c> so the
    /// persisted config values stay stable even if the enum identifiers are renamed.
    /// Inverse of <see cref="DaemonConfig.ParseUpdateChannel(string)"/>.
    /// </summary>
    public static string ToWireValue(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "stable",
        UpdateChannel.Beta => "beta",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Unknown UpdateChannel value: {channel}")
    };
}
