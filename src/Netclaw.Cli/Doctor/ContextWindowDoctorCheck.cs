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
        // overlay keyed by "{provider}/{modelId}" (case-insensitive). The
        // runtime daemon uses exactly this precedence, so the doctor must
        // match it or it will report "no explicit setting" while the
        // daemon honors an override.
        var contextWindow = main["ContextWindow"]
            ?? TryReadCatalogContextWindow(models!, providerName, modelId);

        if (contextWindow is null)
            return await ResolveEffectiveContextWindowAsync(modelId, providerName, cancellationToken);

        if (!TryReadPositiveInt(contextWindow, out var cw))
        {
            return DoctorCheckResult.Error(
                "Context Window",
                $"ContextWindow for {providerName}/{modelId} is not a positive integer (got {DescribeJsonValue(contextWindow)}).",
                "Edit netclaw.json so the ContextWindow value is a positive integer literal (no quotes, no decimals).");
        }

        return DoctorCheckResult.Pass(
            "Context Window",
            $"Context window explicitly set to {cw:N0} tokens.");
    }

    private static JsonNode? TryReadCatalogContextWindow(
        JsonObject models, string providerName, string modelId)
    {
        var catalog = models["Catalog"] as JsonObject;
        if (catalog is null) return null;
        var key = ModelSelection.CatalogKey(providerName, modelId);
        // Case-insensitive scan: ApplyCatalogOverlays does the same when the
        // exact-case lookup misses, so the doctor must agree.
        if (catalog[key] is JsonObject direct) return direct["ContextWindow"];
        foreach (var kvp in catalog)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase)
                && kvp.Value is JsonObject entry)
                return entry["ContextWindow"];
        }
        return null;
    }

    private static bool TryReadPositiveInt(JsonNode node, out int value)
    {
        value = 0;
        try
        {
            // JsonValue.TryGetValue<T> avoids the InvalidOperationException
            // GetValue<int>() throws on strings, doubles, etc. — exactly the
            // shapes a hand-edited Catalog override is prone to.
            if (node is JsonValue jv && jv.TryGetValue<int>(out var parsed) && parsed > 0)
            {
                value = parsed;
                return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // fall through to false
        }
        return false;
    }

    private static string DescribeJsonValue(JsonNode node)
    {
        var raw = node.ToJsonString();
        return raw.Length > 40 ? raw[..37] + "..." : raw;
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
