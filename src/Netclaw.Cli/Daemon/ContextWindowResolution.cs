// -----------------------------------------------------------------------
// <copyright file="ContextWindowResolution.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Resolves the effective context window for the main model by combining
/// the explicit config value with a daemon status query fallback.
/// </summary>
internal static class ContextWindowResolution
{
    /// <summary>
    /// Returns the explicit config value when set; otherwise queries the daemon
    /// status endpoint for the auto-detected context window.
    /// </summary>
    public static async Task<int> ResolveAsync(int? configuredContextWindow, DaemonApi daemon, string modelId)
    {
        if (configuredContextWindow is > 0)
            return configuredContextWindow.Value;

        DaemonRuntimeStatus.Response? status;
        try
        {
            status = await daemon.GetStatusAsync();
        }
        catch (HttpRequestException ex)
        {
            throw new DaemonUnavailableException(daemon.Endpoint, ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new DaemonUnavailableException(daemon.Endpoint, ex);
        }

        if (status is null)
            throw new InvalidOperationException(
                "Daemon returned empty status. Cannot resolve effective context window. " +
                "Set Models.Main.ContextWindow in netclaw.json or ensure the daemon is healthy.");

        return status.Model?.ContextWindow is > 0 and var daemonCw
            ? daemonCw
            : throw new InvalidOperationException(
                $"Daemon reported no context window for model '{modelId}'. " +
                "Set Models.Main.ContextWindow in netclaw.json.");
    }
}

internal sealed class DaemonUnavailableException : InvalidOperationException
{
    public DaemonUnavailableException(string endpoint, Exception innerException)
        : base(
            $"Could not reach the Netclaw daemon at {endpoint}. " +
            "Start it with 'netclaw daemon start' or run 'netclaw doctor' for diagnostics.",
            innerException)
    {
        Endpoint = endpoint;
    }

    public string Endpoint { get; }
}
