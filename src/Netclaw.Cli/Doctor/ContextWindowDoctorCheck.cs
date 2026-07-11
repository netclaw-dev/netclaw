// -----------------------------------------------------------------------
// <copyright file="ContextWindowDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class ContextWindowDoctorCheck : IDoctorCheck
{
    private readonly NetclawPaths _paths;
    private readonly DaemonApi _daemonApi;
    private readonly IConfiguration _configuration;
    private readonly Func<string, string, CancellationToken, Task<int?>> _probeProvider;

    public ContextWindowDoctorCheck(NetclawPaths paths, DaemonApi daemonApi, IConfiguration configuration)
        : this(paths, daemonApi, configuration, (modelId, provider, ct) => ContextWindowDoctorProbe.ProbeAsync(paths, modelId, provider, ct))
    {
    }

    internal ContextWindowDoctorCheck(
        NetclawPaths paths,
        DaemonApi daemonApi,
        IConfiguration configuration,
        Func<string, string, CancellationToken, Task<int?>> probeProvider)
    {
        _paths = paths;
        _daemonApi = daemonApi;
        _configuration = configuration;
        _probeProvider = probeProvider;
    }

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(_paths);
        if (error is not null)
            return error;

        if (root is null)
            return DoctorCheckResult.Pass("Context Window", "No config file to check.");

        var resolvedModels = ModelConfigurationResolver.Resolve(_configuration).Selection;
        var main = resolvedModels.Main;

        var runtimeValidation = ValidateRuntimeConfiguration(root);
        if (runtimeValidation.Status != ProviderRuntimeStatus.Valid)
        {
            return DoctorCheckResult.Warning(
                "Context Window",
                $"Context window unavailable because inference configuration is not valid: {runtimeValidation.Reason}.",
                BuildInferenceRemediation(runtimeValidation.AvailableProviders));
        }

        if (string.IsNullOrWhiteSpace(main.Provider) || string.IsNullOrWhiteSpace(main.ModelId))
        {
            return DoctorCheckResult.Warning(
                "Context Window",
                "No Models.Main section in config. Context window cannot be resolved until a model is selected.",
                "Run `netclaw init` to configure a provider and main model, or add Models.Main to netclaw.json.");
        }

        if (main.ContextWindow is null)
        {
            return await ResolveEffectiveContextWindowAsync(main.ModelId, main.Provider, cancellationToken);
        }

        if (main.ContextWindow is > 0 and var cw)
        {
            // Runtime (ContextWindowResolution.ResolveRuntimeAsync) prefers the
            // daemon's live context window over the pinned config when the daemon
            // is reachable. Reconcile here so the diagnostic matches what chat uses.
            var daemonCw = await TryGetDaemonContextWindowAsync(cancellationToken);
            if (daemonCw is > 0 and var live && live != cw)
            {
                return DoctorCheckResult.Warning(
                    "Context Window",
                    $"Pinned to {cw:N0} tokens, but the running daemon reports {live:N0} tokens, " +
                    "which takes precedence at runtime.",
                    $"Update Models.Main.ContextWindow to {live:N0}, or restart the daemon on the pinned model.");
            }

            return DoctorCheckResult.Pass(
                "Context Window",
                $"Context window explicitly set to {cw:N0} tokens.");
        }

        return DoctorCheckResult.Error(
            "Context Window",
            "Models.Main.ContextWindow must be a positive integer.",
            "Set Models.Main.ContextWindow to the effective runtime context window size in tokens.");
    }

    private ProviderRuntimeValidation ValidateRuntimeConfiguration(JsonObject root)
    {
        var providers = ProviderConfigurationLoader.Load(_configuration.GetSection("Providers"));
        var models = ModelConfigurationResolver.Resolve(_configuration).Selection;

        return ProviderRuntimeValidation.Evaluate(
            providers,
            models,
            ProviderRuntimeConfiguration.FromJson(root));
    }

    private static bool TryGetInt32(JsonNode node, out int value)
    {
        try
        {
            value = node.GetValue<int>();
            return true;
        }
        catch (InvalidOperationException)
        {
            value = 0;
            return false;
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }
    }

    private async Task<int?> TryGetDaemonContextWindowAsync(CancellationToken ct)
    {
        try
        {
            var status = await _daemonApi.GetStatusAsync(ct);
            return status?.Model?.ContextWindow is > 0 and var cw ? cw : null;
        }
        catch (Exception)
        {
            // Daemon unreachable is expected when running doctor offline; the pinned
            // value stands and there is nothing to reconcile against.
            return null;
        }
    }

    private static string BuildInferenceRemediation(IReadOnlyList<string> availableProviders)
    {
        return availableProviders.Count == 0
            ? "Run `netclaw init` to configure a provider and main model, then rerun `netclaw doctor`."
            : "Run `netclaw model` to pick one of the configured providers and a main model, then rerun `netclaw doctor`.";
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
