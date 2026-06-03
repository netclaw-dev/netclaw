// -----------------------------------------------------------------------
// <copyright file="DaemonControlPlaneEndpointResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>
/// Shared helpers for deriving a local control-plane endpoint and deciding whether
/// loopback clients still need explicit bearer authentication.
/// </summary>
public static class DaemonControlPlaneEndpointResolver
{
    public static readonly string DefaultEndpoint = $"http://127.0.0.1:{DaemonConfig.DefaultPort}";

    /// <summary>
    /// Resolves the daemon control-plane endpoint using the standard precedence
    /// shared by all in-tree thin clients (CLI, Web):
    /// <list type="number">
    ///   <item><c>NETCLAW_DAEMON_ENDPOINT</c> environment variable</item>
    ///   <item><c>&lt;basePath&gt;/client/config.json</c> <c>Endpoint</c> field</item>
    ///   <item><c>Daemon</c> block of <c>&lt;basePath&gt;/config/netclaw.json</c></item>
    ///   <item><see cref="DefaultEndpoint"/></item>
    /// </list>
    /// </summary>
    public static string ResolveEndpoint(NetclawPaths? paths = null)
    {
        paths ??= new NetclawPaths();

        return (Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT")
            ?? ReadClientConfigEndpoint(paths)
            ?? ResolveFallbackEndpoint(LoadDaemonConfig(paths))
            ?? DefaultEndpoint).TrimEnd('/');
    }

    /// <summary>
    /// Loads the <see cref="DaemonConfig"/> from <c>netclaw.json</c> and
    /// <c>secrets.json</c> using the same precedence as the daemon process.
    /// </summary>
    public static DaemonConfig LoadDaemonConfig(NetclawPaths paths)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .AddJsonFile(paths.SecretsPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NETCLAW_")
            .Build();

        return DaemonConfig.BindFromConfiguration(config.GetSection("Daemon"));
    }

    private static string? ReadClientConfigEndpoint(NetclawPaths paths)
    {
        if (!File.Exists(paths.ClientConfigPath))
            return null;

        try
        {
            var text = File.ReadAllText(paths.ClientConfigPath);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty("Endpoint", out var endpointProp))
                return null;
            var endpoint = endpointProp.GetString();
            return string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.TrimEnd('/');
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string ResolveFallbackEndpoint(DaemonConfig daemonConfig)
    {
        var host = NormalizeConnectHost(daemonConfig.Host);
        var formattedHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return $"http://{formattedHost}:{daemonConfig.Port}";
    }

    public static string NormalizeConnectHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "127.0.0.1";

        var trimmed = host.Trim().Trim('[', ']');
        return trimmed switch
        {
            "0.0.0.0" => "127.0.0.1",
            "::" => "127.0.0.1",
            _ => trimmed
        };
    }

    public static bool RequiresBearerToken(ExposureMode exposureMode)
        => exposureMode.RequiresRemoteAuthentication();
}
