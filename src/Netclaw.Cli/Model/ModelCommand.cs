// -----------------------------------------------------------------------
// <copyright file="ModelCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Cli.Provider;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Cli.Model;

/// <summary>
/// Handles <c>netclaw model</c> CLI subcommands: list, set, discover, clear.
/// </summary>
internal static class ModelCommand
{
    public static async Task<int> RunAsync(
        string[] args, NetclawPaths paths,
        IProviderProbe? probe = null, TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "list" => RunList(paths, writer),
            "set" => await RunSetAsync(args, paths, probe, writer),
            "discover" => await RunDiscoverAsync(args, paths, probe, writer),
            "clear" => RunClear(args, paths, writer),
            "help" or "-h" or "--help" => WriteHelp(writer),
            _ => WriteHelp(writer)
        };
    }

    private static int RunList(NetclawPaths paths, TextWriter writer)
    {
        var models = LoadModelSelection(paths);
        if (models is null)
        {
            writer.WriteLine("No models configured.");
            writer.WriteLine("Run `netclaw model set` or `netclaw model` (TUI) to configure models.");
            return 0;
        }

        writer.WriteLine($"{"Role",-12} {"Provider",-20} {"Model ID",-30} {"Context Window"}");

        WriteModelRow("Main", models.Main, writer);

        if (models.Fallback is not null)
            WriteModelRow("Fallback", models.Fallback, writer);
        else
            writer.WriteLine($"{"Fallback",-12} {"(not set)",-20}");

        if (models.Compaction is not null)
            WriteModelRow("Compaction", models.Compaction, writer);
        else
            writer.WriteLine($"{"Compaction",-12} {"(not set)",-20}");

        return 0;
    }

    private static void WriteModelRow(string role, ModelReference model, TextWriter writer)
    {
        var ctxWindow = model.ContextWindow.HasValue
            ? $"{model.ContextWindow.Value:N0} tokens"
            : "(default)";
        writer.WriteLine($"{role,-12} {model.Provider,-20} {model.ModelId,-30} {ctxWindow}");
    }

    private static async Task<int> RunSetAsync(
        string[] args, NetclawPaths paths, IProviderProbe? probe, TextWriter writer)
    {
        // Parse: netclaw model set <role> <provider> <model-id>
        //        [--context-window <tokens>] [--input-modalities <list>]
        //        [--output-modalities <list>] [--clear-modalities]
        if (args.Length < 5)
        {
            writer.WriteLine("Usage: netclaw model set <role> <provider> <model-id> [--context-window <tokens>]");
            writer.WriteLine("                       [--input-modalities <list>] [--output-modalities <list>] [--clear-modalities]");
            writer.WriteLine();
            writer.WriteLine("Roles: main, fallback, compaction");
            return 1;
        }

        var role = args[2].ToLowerInvariant();
        var providerName = args[3];
        var modelId = args[4];
        int? contextWindow = null;
        ModelModality? inputModalitySet = null;
        ModelModality? outputModalitySet = null;
        var clearModalities = false;

        for (var i = 5; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--context-window" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var cw) || cw <= 0)
                    {
                        writer.WriteLine("Error: --context-window must be a positive integer.");
                        return 1;
                    }

                    contextWindow = cw;
                    break;
                case "--input-modalities" when i + 1 < args.Length:
                    if (!TryParseModalities(args[++i], out var input, out var inputError))
                    {
                        writer.WriteLine(inputError);
                        return 1;
                    }

                    inputModalitySet = input;
                    break;
                case "--output-modalities" when i + 1 < args.Length:
                    if (!TryParseModalities(args[++i], out var outputModality, out var outputError))
                    {
                        writer.WriteLine(outputError);
                        return 1;
                    }

                    outputModalitySet = outputModality;
                    break;
                case "--clear-modalities":
                    clearModalities = true;
                    break;
            }
        }

        // An explicit --input/--output-modalities set wins over --clear-modalities (regardless of
        // arg order); --clear-modalities applies to whichever side was not explicitly set.
        var inputOverride = inputModalitySet is { } iv
            ? ModalityOverride.Set(iv)
            : clearModalities ? ModalityOverride.Clear : ModalityOverride.Unset;
        var outputOverride = outputModalitySet is { } ov
            ? ModalityOverride.Set(ov)
            : clearModalities ? ModalityOverride.Clear : ModalityOverride.Unset;

        // Validate role
        var roleKey = role switch
        {
            "main" => "Main",
            "fallback" => "Fallback",
            "compaction" => "Compaction",
            _ => null
        };

        if (roleKey is null)
        {
            writer.WriteLine($"Error: Unknown role '{role}'. Valid roles: main, fallback, compaction");
            return 1;
        }

        // Validate provider exists
        var providers = ProviderCommand.LoadProviders(paths);
        if (!providers.TryGetValue(providerName, out var providerEntry))
        {
            writer.WriteLine($"Error: Provider '{providerName}' not found in configuration.");
            writer.WriteLine("Configured providers: " +
                (providers.Count > 0
                    ? string.Join(", ", providers.Keys)
                    : "(none — run `netclaw provider add` first)"));
            return 1;
        }

        // Check for context window downgrade
        var currentModels = LoadModelSelection(paths);
        if (roleKey == "Main" && currentModels?.Main.ContextWindow is > 0 && contextWindow.HasValue)
        {
            if (contextWindow.Value < currentModels.Main.ContextWindow.Value)
            {
                writer.WriteLine($"Warning: Context window shrinking from {currentModels.Main.ContextWindow.Value:N0} to {contextWindow.Value:N0} tokens.");
                writer.WriteLine("         Existing sessions with longer history may fail until compacted.");
            }
        }

        // Explicitly-supplied metadata (a context window or any modality intent) means the
        // operator is configuring manually, so skip the probe — mirroring how --context-window
        // has always short-circuited it. The supplied values win over discovery anyway.
        var manualMetadataSupplied = contextWindow.HasValue || inputOverride.Supplied || outputOverride.Supplied;

        DiscoveredModel? discoveredModel = null;
        var provenance = ModelDiscoverySource.Manual;
        if (!manualMetadataSupplied && ShouldProbeForMetadata(providerEntry))
        {
            probe ??= ProviderCommand.CreateDefaultRegistry();
            ProviderProbeResult probeResult;
            await using (ProbeProgressReporter.Start(ResolveProbeEndpoint(probe, providerEntry)))
                probeResult = await probe.ProbeAsync(providerEntry, CancellationToken.None);
            if (!probeResult.Success)
            {
                writer.WriteLine($"Error: Could not resolve model metadata from provider: {probeResult.ErrorMessage}");
                writer.WriteLine("       Use --context-window only if you want to configure the model manually.");
                return 1;
            }

            discoveredModel = probeResult.Models.FirstOrDefault(m =>
                string.Equals(m.ModelId.Value, modelId, StringComparison.OrdinalIgnoreCase));
            if (discoveredModel is null)
            {
                writer.WriteLine($"Error: Model '{modelId}' was not returned by provider '{providerName}'.");
                writer.WriteLine("       Run `netclaw model discover <provider>` or use --context-window to configure it manually.");
                return 1;
            }

            provenance = ModelDiscoverySource.Live;
        }

        // Write to config
        var (config, _) = ConfigFileHelper.LoadConfigFiles(paths);
        var modelsSection = ConfigFileHelper.GetOrCreateSection(config, "Models");

        // Non-destructive: stored ContextWindow and modalities are operator-owned overrides that
        // discovery never clobbers on a same-model re-set (#1127, #1610). Explicit operator input
        // (--context-window / --input-modalities / --clear-modalities) wins; the probe result only
        // seeds a first-time set or a model switch.
        ModelEntryWriter.WriteRole(
            modelsSection,
            roleKey,
            providerName,
            modelId,
            provenance,
            contextWindow,
            inputOverride,
            outputOverride,
            discoveredModel);
        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);

        writer.WriteLine($"Set {role} model to {providerName}/{modelId}");
        return 0;
    }

    private static bool ShouldProbeForMetadata(ProviderEntry entry)
        => string.Equals(entry.Type, "openai", StringComparison.OrdinalIgnoreCase)
           && entry.AuthMethod is AuthMethod.OAuthDevice or AuthMethod.OAuthPkce;

    /// <summary>
    /// Parses a <c>--input/output-modalities</c> value: a comma-separated list of
    /// <see cref="ModelModality"/> flags (e.g. <c>Text</c>, <c>"Text, Image"</c>). Rejects
    /// <c>None</c> and any unknown flag so only schema-valid overrides reach config — a model
    /// with no modalities is meaningless, and <c>--clear-modalities</c> is the way to remove one.
    /// </summary>
    private static bool TryParseModalities(string value, out ModelModality modalities, out string error)
    {
        const ModelModality all = ModelModality.Text | ModelModality.Image | ModelModality.Audio | ModelModality.Video;
        modalities = default;

        if (Enum.TryParse(value, ignoreCase: true, out ModelModality parsed)
            && parsed != ModelModality.None
            && (parsed & ~all) == 0)
        {
            modalities = parsed;
            error = string.Empty;
            return true;
        }

        error = $"Error: invalid modalities '{value}'. Use a comma-separated list of: Text, Image, Audio, Video "
                + "(or --clear-modalities to remove the override).";
        return false;
    }

    /// <summary>
    /// The endpoint the probe will actually hit, for display. When a provider has no
    /// explicit endpoint we surface the descriptor's default (e.g. a self-hosted
    /// provider that silently fell back to localhost) so the target is visible rather
    /// than hidden behind an anonymous wait (#1292).
    /// </summary>
    private static string ResolveProbeEndpoint(IProviderProbe probe, ProviderEntry entry)
    {
        // Match the normalization ExecuteProbeAsync applies (it trims a trailing slash
        // before appending the model-listing path) so the surfaced endpoint is the one
        // actually probed, not a cosmetically different string.
        if (!string.IsNullOrWhiteSpace(entry.Endpoint))
            return entry.Endpoint.TrimEnd('/');

        return probe is ProviderDescriptorRegistry registry
               && registry.TryGet(entry.Type, out var descriptor)
            ? descriptor.DefaultEndpoint
            : "(default endpoint)";
    }

    private static async Task<int> RunDiscoverAsync(string[] args, NetclawPaths paths, IProviderProbe? probe, TextWriter writer)
    {
        if (args.Length < 3)
        {
            writer.WriteLine("Usage: netclaw model discover <provider>");
            return 1;
        }

        var providerName = args[2];
        var providers = ProviderCommand.LoadProviders(paths);

        if (!providers.TryGetValue(providerName, out var entry))
        {
            writer.WriteLine($"Error: Provider '{providerName}' not found in configuration.");
            return 1;
        }

        // Use the registry as the probe (it implements IProviderProbe)
        probe ??= ProviderCommand.CreateDefaultRegistry();

        var probeEndpoint = ResolveProbeEndpoint(probe, entry);
        writer.WriteLine($"Discovering models from '{providerName}' ({entry.Type}) at {probeEndpoint}...");

        ProviderProbeResult result;
        await using (ProbeProgressReporter.Start(probeEndpoint))
            result = await probe.ProbeAsync(entry, CancellationToken.None);

        if (!result.Success)
        {
            writer.WriteLine($"Error: {result.ErrorMessage}");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            writer.WriteLine($"Warning: {result.ErrorMessage}");

        if (result.Models.Count == 0)
        {
            writer.WriteLine("No models found.");
            return 0;
        }

        writer.WriteLine();
        writer.WriteLine($"{"Model ID",-40} {"Context Window",-16} {"Cost (in/out per 1M)"}");
        foreach (var model in result.Models.OrderBy(m => m.ModelId.Value, StringComparer.OrdinalIgnoreCase))
        {
            var ctx = model.ContextWindowTokens.HasValue
                ? $"{model.ContextWindowTokens.Value:N0}"
                : "-";
            var cost = (model.CostPerMillionInputTokens, model.CostPerMillionOutputTokens) switch
            {
                (not null, not null) => $"${model.CostPerMillionInputTokens:F2} / ${model.CostPerMillionOutputTokens:F2}",
                _ => "-"
            };
            writer.WriteLine($"{model.ModelId,-40} {ctx,-16} {cost}");
        }

        writer.WriteLine();
        writer.WriteLine($"{result.Models.Count} model(s) found.");
        return 0;
    }

    private static int RunClear(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (args.Length < 3)
        {
            writer.WriteLine("Usage: netclaw model clear <role>");
            writer.WriteLine();
            writer.WriteLine("Roles: fallback, compaction (cannot clear main)");
            return 1;
        }

        var role = args[2].ToLowerInvariant();

        if (role is "main")
        {
            writer.WriteLine("Error: Cannot clear the main model role. Use `netclaw model set main` to change it instead.");
            return 1;
        }

        var roleKey = role switch
        {
            "fallback" => "Fallback",
            "compaction" => "Compaction",
            _ => null
        };

        if (roleKey is null)
        {
            writer.WriteLine($"Error: Unknown role '{role}'. Valid roles for clear: fallback, compaction");
            return 1;
        }

        var (config, _) = ConfigFileHelper.LoadConfigFiles(paths);
        var modelsSection = ConfigFileHelper.GetSectionOrNull(config, "Models");

        if (modelsSection is null || !modelsSection.ContainsKey(roleKey))
        {
            writer.WriteLine($"Role '{role}' is not configured.");
            return 0;
        }

        modelsSection.Remove(roleKey);
        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);

        writer.WriteLine($"Cleared {role} model role.");
        return 0;
    }

    /// <summary>
    /// Load model selection from config file.
    /// </summary>
    internal static ModelSelection? LoadModelSelection(NetclawPaths paths)
    {
        if (!File.Exists(paths.NetclawConfigPath))
            return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(paths.NetclawConfigPath));
        if (!doc.RootElement.TryGetProperty("Models", out var modelsElement))
            return null;

        return JsonSerializer.Deserialize<ModelSelection>(modelsElement.GetRawText(), JsonDefaults.EnumAware);
    }

    private static int WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Usage: netclaw model <subcommand>");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  list                                     Show current model assignments");
        writer.WriteLine("  set <role> <provider> <model-id>         Assign model to role");
        writer.WriteLine("  discover <provider>                      List available models from provider");
        writer.WriteLine("  clear <role>                             Clear fallback or compaction role");
        writer.WriteLine();
        writer.WriteLine("Run `netclaw model` (no subcommand) for interactive TUI management.");
        writer.WriteLine();
        writer.WriteLine("Roles: main, fallback, compaction");
        writer.WriteLine();
        writer.WriteLine("Options for 'set':");
        writer.WriteLine("  --context-window <tokens>    Override context window size");
        writer.WriteLine("  --input-modalities <list>    Override input modalities, e.g. \"Text, Image\"");
        writer.WriteLine("  --output-modalities <list>   Override output modalities, e.g. \"Text\"");
        writer.WriteLine("  --clear-modalities           Remove modality overrides (fall back to runtime detection)");
        writer.WriteLine();
        writer.WriteLine("  Modality and context-window overrides are preserved when you re-set the same model;");
        writer.WriteLine("  provider discovery never overwrites them. Use these flags (or --clear-modalities) to change them.");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  netclaw model list");
        writer.WriteLine("  netclaw model discover my-ollama");
        writer.WriteLine("  netclaw model set main my-ollama qwen3:30b --context-window 32768");
        writer.WriteLine("  netclaw model set main my-vllm qwen-vl --input-modalities \"Text, Image\"");
        writer.WriteLine("  netclaw model set main my-ollama qwen3:30b --clear-modalities");
        writer.WriteLine("  netclaw model clear fallback");
        return 0;
    }
}
