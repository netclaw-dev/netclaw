// -----------------------------------------------------------------------
// <copyright file="ContextWindowResolution.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

        var status = await daemon.GetStatusAsync()
            ?? throw new InvalidOperationException(
                "Daemon returned empty status. Cannot resolve effective context window. " +
                "Set Models.Main.ContextWindow in netclaw.json or ensure the daemon is healthy.");
        return status.Model?.ContextWindow is > 0 and var daemonCw
            ? daemonCw
            : throw new InvalidOperationException(
                $"Daemon reported no context window for model '{modelId}'. " +
                "Set Models.Main.ContextWindow in netclaw.json.");
    }
}
