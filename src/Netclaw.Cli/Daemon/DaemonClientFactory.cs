// -----------------------------------------------------------------------
// <copyright file="DaemonClientFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Factory helpers for creating <see cref="DaemonClient"/> instances with the
/// appropriate access token provider based on the daemon endpoint.
/// <para>
/// For loopback endpoints (<c>127.0.0.1</c> / <c>localhost</c>), no token is
/// required — the <see cref="LoopbackAuthenticationHandler"/> trusts local connections.
/// </para>
/// <para>
/// For non-loopback (remote) endpoints, the device token is read from
/// <c>secrets.json</c>, decrypted if necessary, and attached as the bearer token
/// provider passed to <see cref="DaemonClient"/>.
/// </para>
/// </summary>
internal static class DaemonClientFactory
{
    /// <summary>
    /// Creates a <see cref="DaemonClient"/> for the given endpoint, attaching a
    /// bearer token for non-loopback connections if a <c>DeviceToken</c> is present
    /// in <c>secrets.json</c>.
    /// </summary>
    internal static DaemonClient Create(string endpoint, NetclawPaths paths)
    {
        var accessTokenProvider = CreateAccessTokenProvider(endpoint, paths, ResolveExposureMode(paths));
        return new DaemonClient(endpoint, accessTokenProvider: accessTokenProvider);
    }

    /// <summary>
    /// Returns a bearer token provider for non-loopback endpoints, or <c>null</c>
    /// for loopback connections. Internal for unit testing.
    /// </summary>
    internal static Func<Task<string?>>? CreateAccessTokenProvider(
        string endpoint, NetclawPaths paths, ExposureMode exposureMode)
    {
        var token = ResolveDeviceToken(endpoint, paths, exposureMode);
        return token is not null
            ? () => Task.FromResult<string?>(token)
            : null;
    }

    /// <summary>
    /// Resolves the device bearer token for a non-loopback endpoint, or <c>null</c>
    /// when the endpoint is loopback or no token is configured.
    /// </summary>
    internal static string? ResolveDeviceToken(string endpoint, NetclawPaths paths, ExposureMode exposureMode)
    {
        if (IsLoopback(endpoint) && !DaemonControlPlaneEndpointResolver.RequiresBearerToken(exposureMode))
            return null;

        return ReadDeviceToken(paths);
    }

    internal static ExposureMode ResolveExposureMode(NetclawPaths paths)
        => LoadDaemonConfig(paths).ExposureMode;

    internal static DaemonConfig LoadDaemonConfig(NetclawPaths paths)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .AddJsonFile(paths.SecretsPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NETCLAW_")
            .Build();

        return DaemonConfig.BindFromConfiguration(config.GetSection("Daemon"));
    }

    /// <summary>
    /// Returns <c>true</c> if the endpoint resolves to a loopback address
    /// (<c>127.0.0.1</c> or <c>localhost</c>).
    /// </summary>
    internal static bool IsLoopback(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return true;

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return uri.IsLoopback;

        // Fallback for malformed URIs
        return endpoint.Contains("127.0.0.1", StringComparison.Ordinal)
            || endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads and decrypts the <c>DeviceToken</c> from <c>secrets.json</c>.
    /// Returns <c>null</c> if the file doesn't exist or the key is absent.
    /// </summary>
    private static string? ReadDeviceToken(NetclawPaths paths)
    {
        if (!File.Exists(paths.SecretsPath))
            return null;

        var dict = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
        if (!dict.TryGetValue("DeviceToken", out var val))
            return null;

        var raw = val is JsonElement je ? je.GetString() : val?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return ConfigFileHelper.DecryptIfEncrypted(paths, raw);
    }
}
