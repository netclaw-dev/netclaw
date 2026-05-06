// -----------------------------------------------------------------------
// <copyright file="DaemonControlPlaneEndpointResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Shared helpers for deriving a local control-plane endpoint and deciding whether
/// loopback clients still need explicit bearer authentication.
/// </summary>
public static class DaemonControlPlaneEndpointResolver
{
    public const string DefaultEndpoint = "http://127.0.0.1:5199";

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
