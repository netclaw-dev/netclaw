// -----------------------------------------------------------------------
// <copyright file="ContextWindowDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class ContextWindowDoctorCheck : IDoctorCheck
{
    private readonly NetclawPaths _paths;
    private readonly DaemonApi _daemonApi;
    private readonly Func<string, string, CancellationToken, Task<int?>> _probeProvider;

    public ContextWindowDoctorCheck(NetclawPaths paths, DaemonApi daemonApi)
        : this(paths, daemonApi, (modelId, provider, ct) => ContextWindowDoctorProbe.ProbeAsync(paths, modelId, provider, ct))
    {
    }

    internal ContextWindowDoctorCheck(
        NetclawPaths paths,
        DaemonApi daemonApi,
        Func<string, string, CancellationToken, Task<int?>> probeProvider)
    {
        _paths = paths;
        _daemonApi = daemonApi;
        _probeProvider = probeProvider;
    }

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(_paths);
        if (error is not null)
            return error;

        if (root is null)
            return DoctorCheckResult.Pass("Context Window", "No config file to check.");

        var models = root["Models"] as JsonObject;
        var main = models?["Main"] as JsonObject;

        if (main is null)
        {
            return DoctorCheckResult.Warning(
                "Context Window",
                "No Models.Main section in config. Using default context window (32,768 tokens).",
                "Add a Models.Main section with ContextWindow to netclaw.json.");
        }

        var modelId = main["ModelId"]?.GetValue<string>() ?? "unknown";
        var providerName = main["Provider"]?.GetValue<string>() ?? "local-ollama";

        // Effective ContextWindow follows ModelSelection.ApplyCatalogOverlays:
        // inline on the role wins; otherwise fall back to the catalog
        // overlay keyed by "{provider}/{modelId}". The runtime daemon uses
        // exactly this precedence, so the doctor must match it or it will
        // report "no explicit setting" while the daemon honors an override.
        var contextWindow = main["ContextWindow"];
        if (contextWindow is null && models is not null)
            contextWindow = TryReadCatalogContextWindow(models, providerName, modelId);

        if (contextWindow is null)
            return await ResolveEffectiveContextWindowAsync(modelId, providerName, cancellationToken);

        if (contextWindow.GetValue<int>() is var cw and > 0)
        {
            return DoctorCheckResult.Pass(
                "Context Window",
                $"Context window explicitly set to {cw:N0} tokens.");
        }

        return DoctorCheckResult.Error(
            "Context Window",
            "Models.Main.ContextWindow must be a positive integer.",
            "Set Models.Main.ContextWindow to the effective runtime context window size in tokens.");
    }

    private static JsonNode? TryReadCatalogContextWindow(
        JsonObject models, string providerName, string modelId)
    {
        var catalog = models["Catalog"] as JsonObject;
        if (catalog is null) return null;
        var entry = catalog[ModelSelection.CatalogKey(providerName, modelId)] as JsonObject;
        return entry?["ContextWindow"];
    }

    private async Task<DoctorCheckResult> ResolveEffectiveContextWindowAsync(
        string modelId, string providerName, CancellationToken ct)
    {
        string? daemonError = null;
        try
        {
            var status = await _daemonApi.GetStatusAsync(ct);
            if (status?.Model?.ContextWindow is > 0 and var daemonCw)
            {
                return DoctorCheckResult.Pass(
                    "Context Window",
                    $"Auto-detected {daemonCw:N0} tokens for {modelId} (from running daemon).");
            }
        }
        catch (Exception ex)
        {
            daemonError = ex.GetType().Name;
        }

        string? probeError = null;
        try
        {
            var probed = await _probeProvider(modelId, providerName, ct);
            if (probed is > 0 and var probedCw)
            {
                return DoctorCheckResult.Pass(
                    "Context Window",
                    $"Auto-detected {probedCw:N0} tokens for {modelId} (from provider).");
            }

            probeError = "provider returned no context window";
        }
        catch (Exception ex)
        {
            probeError = $"provider probe failed: {ex.GetType().Name}";
        }

        var reasons = new List<string>(2);
        if (daemonError is not null) reasons.Add($"daemon: {daemonError}");
        if (probeError is not null) reasons.Add(probeError);

        return DoctorCheckResult.Warning(
            "Context Window",
            $"Could not detect context window for {modelId} ({string.Join("; ", reasons)}). " +
            "At runtime, the daemon will attempt auto-detection from the provider.",
            "Set Models.Main.ContextWindow in netclaw.json to pin a specific value.");
    }
}
