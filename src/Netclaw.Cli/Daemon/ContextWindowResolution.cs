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
    public static async Task<ModelRuntimeResolution> ResolveRuntimeAsync(ModelReference configuredMain, DaemonApi daemon)
    {
        DaemonRuntimeStatus.Response? status;
        try
        {
            status = await GetStatusAsync(daemon);
        }
        catch (DaemonUnavailableException) when (configuredMain.ContextWindow is > 0)
        {
            return Configured(configuredMain);
        }

        if (status?.Model is { ContextWindow: > 0 } daemonModel)
        {
            return new ModelRuntimeResolution(
                daemonModel.ModelId,
                daemonModel.Provider,
                daemonModel.ContextWindow);
        }

        // The daemon was reachable but gave us no usable context window — either an
        // empty status body (status is null) or a model without one. Fall back to the
        // configured value when set, exactly as the daemon-unavailable path above
        // does; otherwise the empty-status case would crash a user who pinned a window.
        if (configuredMain.ContextWindow is > 0)
            return Configured(configuredMain);

        throw new InvalidOperationException(
            status is null
                ? "Daemon returned empty status. Cannot resolve effective context window. " +
                  "Set Models.Main.ContextWindow in netclaw.json or ensure the daemon is healthy."
                : $"Daemon reported no context window for model '{configuredMain.ModelId}'. " +
                  "Set Models.Main.ContextWindow in netclaw.json.");
    }

    private static ModelRuntimeResolution Configured(ModelReference configuredMain)
        => new(configuredMain.ModelId, configuredMain.Provider, configuredMain.ContextWindow!.Value);

    private static async Task<DaemonRuntimeStatus.Response?> GetStatusAsync(DaemonApi daemon)
    {
        try
        {
            return await daemon.GetStatusAsync();
        }
        catch (HttpRequestException ex)
        {
            throw new DaemonUnavailableException(daemon.Endpoint, ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new DaemonUnavailableException(daemon.Endpoint, ex);
        }
    }
}

internal sealed record ModelRuntimeResolution(
    string ModelId,
    string Provider,
    int ContextWindowTokens);

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
