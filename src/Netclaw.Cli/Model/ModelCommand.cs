// -----------------------------------------------------------------------
// <copyright file="ModelCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
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
    /// <summary>Short one-line form for TUI status areas; the detail comes from <c>netclaw doctor</c>.</summary>
    internal const string InvalidConfigurationMessage = "Model configuration is invalid. Run `netclaw doctor` for details.";

    public static async Task<int> RunAsync(
        string[] args, NetclawPaths paths,
        IProviderProbe? probe = null, TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        var subcommand = args.Length > 1 ? args[1] : "help";

        // Each subcommand takes operands, so a help flag after it would be read as a name.
        if (CliArgsParser.HasTrailingHelpToken(args, startIndex: 2, includeBareHelp: false))
            return WriteHelp(writer);

        try
        {
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
        catch (ModelConfigurationException ex)
        {
            writer.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int RunList(NetclawPaths paths, TextWriter writer)
    {
        // Display only: the daemon layers NETCLAW_* over netclaw.json, so the list shows that view.
        // Guards and editors keep reading the file, because they change the file.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NETCLAW_")
            .Build();
        if (!TryResolveModelSelection(configuration, out var models, out var error))
        {
            writer.WriteLine($"Error: {error}");
            writer.WriteLine("Fix the Models section in netclaw.json, then rerun `netclaw model list`.");
            return 1;
        }

        if (models is null)
        {
            writer.WriteLine("No models configured.");
            writer.WriteLine("Run `netclaw model set` or `netclaw model` (TUI) to configure models.");
            return 0;
        }

        writer.WriteLine($"{"Role",-12} {"Provider",-20} {"Model ID",-30} {"Context Window",-16} {"Max out",-14} {"Thinking"}");

        WriteModelRow("Main", models.Main, writer);

        if (models.Fallback is not null)
            WriteModelRow("Fallback", models.Fallback, writer);
        else
            writer.WriteLine($"{"Fallback",-12} {"(not set)",-20}");

        if (models.Compaction is not null)
            WriteModelRow("Compaction", models.Compaction, writer);
        else
            writer.WriteLine($"{"Compaction",-12} {"(not set)",-20}");

        var modelVariables = Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(name => name.StartsWith("NETCLAW_Models__", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (modelVariables.Count > 0)
            writer.WriteLine($"Note: this shell's environment ({string.Join(", ", modelVariables)}) is applied. The daemon uses its own environment.");

        return 0;
    }

    private static void WriteModelRow(string role, ModelReference model, TextWriter writer)
    {
        var ctxWindow = model.ContextWindow.HasValue
            ? $"{model.ContextWindow.Value:N0} tokens"
            : "(default)";
        var maxOut = model.MaxOutputTokens is { } limit
            ? $"{limit:N0}"
            : $"default {ModelReference.DefaultMaxOutputTokens}";
        var thinking = model.Thinking is { } level ? ThinkingDialectMap.Format(level) : "default";
        writer.WriteLine($"{role,-12} {model.Provider,-20} {model.ModelId,-30} {ctxWindow,-16} {maxOut,-14} {thinking}");
    }

    private static async Task<int> RunSetAsync(
        string[] args, NetclawPaths paths, IProviderProbe? probe, TextWriter writer)
    {
        // Parse: netclaw model set <role> <provider> <model-id>
        //        [--context-window <tokens>] [--input-modalities <list>]
        //        [--output-modalities <list>] [--clear-modalities]
        if (args.Length < 5)
        {
            writer.WriteLine("Usage: netclaw model set <role> <provider> <model-id> [--context-window <tokens>] [--clear-context-window]");
            writer.WriteLine("                       [--input-modalities <list>] [--output-modalities <list>] [--clear-modalities]");
            writer.WriteLine("                       [--max-output-tokens <tokens>] [--clear-max-output-tokens]");
            writer.WriteLine("                       [--thinking <off|low|medium|high|max>] [--clear-thinking]");
            writer.WriteLine();
            writer.WriteLine("Roles: main, fallback, compaction");
            return 1;
        }

        var role = args[2].ToLowerInvariant();
        var providerName = args[3];
        var modelId = args[4];
        int? contextWindow = null;
        var clearContextWindow = false;
        ModelModality? inputModalitySet = null;
        ModelModality? outputModalitySet = null;
        var clearModalities = false;
        int? maxOutputTokens = null;
        var clearMaxOutputTokens = false;
        ThinkingLevel? thinking = null;
        var clearThinking = false;

        for (var i = 5; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--context-window":
                    if (!TryTakeValue(args, ref i, out var cwRaw))
                    {
                        writer.WriteLine("Error: --context-window requires a value.");
                        return 1;
                    }

                    if (!int.TryParse(cwRaw, out var cw) || cw <= 0)
                    {
                        writer.WriteLine("Error: --context-window must be a positive integer.");
                        return 1;
                    }

                    contextWindow = cw;
                    break;
                case "--clear-context-window":
                    clearContextWindow = true;
                    break;
                case "--input-modalities":
                    if (!TryTakeValue(args, ref i, out var inputRaw))
                    {
                        writer.WriteLine("Error: --input-modalities requires a value.");
                        return 1;
                    }

                    if (!TryParseModalities(inputRaw, out var input, out var inputError))
                    {
                        writer.WriteLine(inputError);
                        return 1;
                    }

                    inputModalitySet = input;
                    break;
                case "--output-modalities":
                    if (!TryTakeValue(args, ref i, out var outputRaw))
                    {
                        writer.WriteLine("Error: --output-modalities requires a value.");
                        return 1;
                    }

                    if (!TryParseModalities(outputRaw, out var outputModality, out var outputError))
                    {
                        writer.WriteLine(outputError);
                        return 1;
                    }

                    outputModalitySet = outputModality;
                    break;
                case "--clear-modalities":
                    clearModalities = true;
                    break;
                case "--max-output-tokens":
                    if (!TryTakeValue(args, ref i, out var limitRaw))
                    {
                        writer.WriteLine("Error: --max-output-tokens requires a value.");
                        return 1;
                    }

                    if (!int.TryParse(limitRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
                    {
                        writer.WriteLine("Error: --max-output-tokens must be a positive integer.");
                        return 1;
                    }

                    maxOutputTokens = limit;
                    break;
                case "--clear-max-output-tokens":
                    clearMaxOutputTokens = true;
                    break;
                case "--thinking":
                    if (!TryTakeValue(args, ref i, out var thinkingRaw))
                    {
                        writer.WriteLine("Error: --thinking requires a value.");
                        return 1;
                    }

                    if (!TryParseThinkingLevel(thinkingRaw, out var level))
                    {
                        writer.WriteLine($"Error: invalid thinking level '{thinkingRaw}'. Use off, low, medium, high, or max (or --clear-thinking).");
                        return 1;
                    }

                    thinking = level;
                    break;
                case "--clear-thinking":
                    clearThinking = true;
                    break;
                default:
                    // Fail loudly on a stray token: a trailing flag with a missing value, a
                    // mistyped flag name, or an unexpected positional would otherwise be silently
                    // ignored while the command still reported success.
                    writer.WriteLine($"Error: unknown argument '{args[i]}'.");
                    return 1;
            }
        }

        if (contextWindow.HasValue && clearContextWindow)
        {
            writer.WriteLine("Error: --context-window and --clear-context-window cannot be combined.");
            return 1;
        }

        if (maxOutputTokens.HasValue && clearMaxOutputTokens)
        {
            writer.WriteLine("Error: --max-output-tokens and --clear-max-output-tokens cannot be combined.");
            return 1;
        }

        if (thinking.HasValue && clearThinking)
        {
            writer.WriteLine("Error: --thinking and --clear-thinking cannot be combined.");
            return 1;
        }

        var maxOutputOverride = maxOutputTokens is { } limitValue
            ? ValueOverride<int>.Set(limitValue)
            : clearMaxOutputTokens ? ValueOverride<int>.Clear : ValueOverride<int>.Unset;
        var thinkingOverride = thinking is { } levelValue
            ? ValueOverride<ThinkingLevel>.Set(levelValue)
            : clearThinking ? ValueOverride<ThinkingLevel>.Clear : ValueOverride<ThinkingLevel>.Unset;
        var settingsChanged = maxOutputOverride.Supplied || thinkingOverride.Supplied;

        // An explicit --context-window sets the clamp; --clear-context-window removes it so
        // detection resolves it; neither leaves the stored value / discovery to decide.
        var contextWindowOverride = contextWindow is { } cwv
            ? ValueOverride<int>.Set(cwv)
            : clearContextWindow ? ValueOverride<int>.Clear : ValueOverride<int>.Unset;

        // An explicit --input/--output-modalities set wins over --clear-modalities (regardless of
        // arg order); --clear-modalities applies to whichever side was not explicitly set.
        var inputOverride = inputModalitySet is { } iv
            ? ValueOverride<ModelModality>.Set(iv)
            : clearModalities ? ValueOverride<ModelModality>.Clear : ValueOverride<ModelModality>.Unset;
        var outputOverride = outputModalitySet is { } ov
            ? ValueOverride<ModelModality>.Set(ov)
            : clearModalities ? ValueOverride<ModelModality>.Clear : ValueOverride<ModelModality>.Unset;

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
        providerName = ConfigFileHelper.ResolveName(providers.Keys, providerName);

        if (!providers.TryGetValue(providerName, out var providerEntry))
        {
            writer.WriteLine($"Error: Provider '{providerName}' not found in configuration.");
            writer.WriteLine("Configured providers: " +
                (providers.Count > 0
                    ? string.Join(", ", providers.Keys)
                    : "(none — run `netclaw provider add` first)"));
            return 1;
        }

        // Check for context window downgrade. LoadModelSelection degrades a corrupt current config
        // to null (this re-set is about to overwrite/repair it), so a bad existing entry skips the
        // advisory warning instead of aborting the repair.
        var currentModels = LoadModelSelection(paths);
        if (roleKey == "Main" && currentModels?.Main.ContextWindow is > 0 && contextWindow.HasValue)
        {
            if (contextWindow.Value < currentModels.Main.ContextWindow.Value)
            {
                writer.WriteLine($"Warning: Context window shrinking from {currentModels.Main.ContextWindow.Value:N0} to {contextWindow.Value:N0} tokens.");
                writer.WriteLine("         Existing sessions with longer history may fail until compacted.");
            }
        }

        // An explicit context window permits a manual model ID.
        // All other OAuth selections require validation against the live model list.
        // The output limit and the thinking level need the model capability, so a settings
        // flag also probes. Detection and capability come only from this probe (design D6).
        var provenance = ModelDiscoverySource.Manual;
        var identityProbe = !contextWindow.HasValue && ShouldProbeForMetadata(providerEntry);
        ProviderProbeResult? probeResult = null;
        DiscoveredModel? discoveredModel = null;
        if (identityProbe || settingsChanged)
        {
            probe ??= ProviderCommand.CreateDefaultRegistry();
            await using (ProbeProgressReporter.Start(ResolveProbeEndpoint(probe, providerEntry)))
                probeResult = await probe.ProbeAsync(providerEntry, CancellationToken.None);
            discoveredModel = probeResult.Success
                ? probeResult.Models.FirstOrDefault(m =>
                    string.Equals(m.ModelId.Value, modelId, StringComparison.OrdinalIgnoreCase))
                : null;

            if (identityProbe)
            {
                if (!probeResult.Success)
                {
                    writer.WriteLine($"Error: Could not resolve model metadata from provider: {probeResult.ErrorMessage}");
                    writer.WriteLine("       Use --context-window only if you want to configure the model manually.");
                    return 1;
                }

                if (discoveredModel is null)
                {
                    writer.WriteLine($"Error: Model '{modelId}' was not returned by provider '{providerName}'.");
                    writer.WriteLine("       Run `netclaw model discover <provider>` or use --context-window to configure it manually.");
                    return 1;
                }

                provenance = ModelDiscoverySource.Live;
            }
            else if (discoveredModel is null)
            {
                writer.WriteLine(probeResult.Success
                    ? $"Warning: provider '{providerName}' did not list model '{modelId}', so its thinking capability and maximum output are unknown."
                    : $"Warning: could not probe provider '{providerName}' ({probeResult.ErrorMessage}), so the thinking capability and the maximum output of the model are unknown.");
            }
        }

        // Write to config
        var (config, _) = ConfigFileHelper.LoadConfigFiles(paths);
        var modelsSection = ConfigFileHelper.GetOrCreateSection(config, "Models");

        // Definitions own model metadata. Role switches preserve another model's overrides.
        // Only explicit operator input persists capability overrides (#1756).
        var definitionName = ModelEntryWriter.WriteRole(
            modelsSection,
            roleKey,
            providerName,
            modelId,
            provenance,
            contextWindowOverride,
            inputOverride,
            outputOverride);

        if (settingsChanged)
            ModelEntryWriter.WriteSettings(modelsSection, definitionName, maxOutputOverride, thinkingOverride);

        var registry = probe as ProviderDescriptorRegistry ?? ProviderCommand.CreateDefaultRegistry();
        if (!registry.TryGet(providerEntry.Type, out var descriptor))
        {
            writer.WriteLine($"Error: Provider '{providerName}' has unknown type '{providerEntry.Type}'.");
            return 1;
        }

        var detectedDialect = settingsChanged ? DetectedDialectToSave(descriptor, providerEntry, probeResult) : null;
        if (detectedDialect is { } detected)
        {
            var vendorOptions = providerEntry.VendorOptions?.DeepClone() as JsonObject ?? new JsonObject();
            vendorOptions[ThinkingDialectMap.VendorOptionKey] = detected.ToString();
            providerEntry.SetVendorOptions(vendorOptions);
        }

        // One validator before persistence, on the whole written definition: a rejected value leaves
        // netclaw.json unchanged. The capability needs the probe, so it is checked only for the field
        // that this command changes.
        var keyPath = $"Models:Definitions:{definitionName}";
        var written = ModelEntryWriter.ReadDefinition(modelsSection, definitionName);
        var settingsError = ModelSettingsValidator.Check(keyPath, providerName, providerEntry, descriptor, written)
            ?? (maxOutputTokens.HasValue
                ? ModelSettingsValidator.CheckModelMax(keyPath, written, discoveredModel?.MaxOutputTokens)
                : null)
            ?? (thinking.HasValue
                ? ModelSettingsValidator.CheckOfferedLevel(
                    providerName, providerEntry, descriptor, written, discoveredModel?.Thinking ?? ThinkingSupport.Unknown)
                : null);
        if (settingsError is not null)
        {
            writer.WriteLine($"Error: {settingsError}");
            return 1;
        }

        if (detectedDialect is { } saved)
        {
            var providerSection = ConfigFileHelper.GetOrCreateSection(
                ConfigFileHelper.GetOrCreateSection(config, "Providers"), providerName);
            ConfigFileHelper.GetOrCreateSection(providerSection, "VendorOptions")[ThinkingDialectMap.VendorOptionKey] = saved.ToString();
            writer.WriteLine($"Detected thinking dialect {saved}; saved Providers:{providerName}:VendorOptions:{ThinkingDialectMap.VendorOptionKey}.");
        }

        if (ModelEntryWriter.OtherRolesOf(modelsSection, roleKey, definitionName) is { Count: > 0 } otherRoles)
            writer.WriteLine($"Note: definition '{definitionName}' is also used by: {string.Join(", ", otherRoles)}. The change applies to each of them.");

        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);

        writer.WriteLine($"Set {role} model to {providerName}/{modelId}");
        return 0;
    }

    /// <summary>
    /// The dialect that the probe detected, when the provider has no dialect value yet. A configured
    /// value always wins, and an invalid value is left for the validator to report.
    /// </summary>
    private static ThinkingDialect? DetectedDialectToSave(
        IProviderDescriptor descriptor, ProviderEntry entry, ProviderProbeResult? probeResult)
    {
        if (probeResult?.DetectedThinkingDialect is not { } detected)
            return null;

        try
        {
            return descriptor.ResolveThinkingDialect(entry) == ThinkingDialect.Unresolved ? detected : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryParseThinkingLevel(string value, out ThinkingLevel level)
    {
        // Named levels only: Enum.TryParse would also accept a number.
        level = default;
        return !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
               && Enum.TryParse(value, ignoreCase: true, out level)
               && Enum.IsDefined(level);
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
        error = $"Error: invalid modalities '{value}'. Use a comma-separated list of: Text, Image, Audio, Video "
                + "(or --clear-modalities to remove the override).";

        var result = ModelModality.None;
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Enum.TryParse accepts underlying integers, including values that happen to be declared
            // members ("1" → Text). The CLI contract is named flags only, so reject numeric tokens
            // before parsing rather than relying on Enum.IsDefined to distinguish composites.
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                || !Enum.TryParse(token, ignoreCase: true, out ModelModality parsed)
                || !Enum.IsDefined(parsed)
                || parsed == ModelModality.None)
                return false;

            result |= parsed;
        }

        if (result == ModelModality.None || (result & ~all) != 0)
            return false;

        modalities = result;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Consumes the value token following a value-requiring flag, advancing <paramref name="i"/>.
    /// Returns false when the flag is the final token (its value is missing) so the caller can
    /// reject it rather than silently dropping the flag.
    /// </summary>
    private static bool TryTakeValue(string[] args, ref int i, out string value)
    {
        if (i + 1 < args.Length)
        {
            value = args[++i];
            return true;
        }

        value = string.Empty;
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
        providerName = ConfigFileHelper.ResolveName(providers.Keys, providerName);

        if (!providers.TryGetValue(providerName, out var entry))
        {
            writer.WriteLine($"Error: Provider '{providerName}' not found in configuration.");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(entry.Endpoint) && !ProviderCommand.IsHttpEndpoint(entry.Endpoint))
        {
            writer.WriteLine($"Error: Provider '{providerName}' has endpoint '{entry.Endpoint}', which is not an absolute http or https URL. "
                + "Fix it by editing the provider's endpoint in `netclaw provider`.");
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

        if (modelsSection is null)
        {
            writer.WriteLine($"Role '{role}' is not configured.");
            return 0;
        }

        bool removed;
        try
        {
            removed = ModelEntryWriter.ClearRole(modelsSection, roleKey);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            writer.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        if (!removed)
        {
            writer.WriteLine($"Role '{role}' is not configured.");
            return 0;
        }

        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);

        writer.WriteLine($"Cleared {role} model role.");
        return 0;
    }

    /// <summary>
    /// Loads the model selection, or null when no config / Models section exists OR the config is
    /// present but unparseable (e.g. a legacy or hand-edited entry with an unrecognized modality
    /// enum string, which the strict enum-aware deserializer rejects by throwing). The <c>model</c>
    /// commands are the repair surface for exactly such configs, so they must degrade rather than
    /// abort with an unhandled JsonException. Callers that must distinguish "absent" from "corrupt"
    /// — to fail loudly instead of showing a corrupt config as empty — use
    /// <see cref="TryLoadModelSelection"/>.
    /// </summary>
    internal static ModelSelection? LoadModelSelection(NetclawPaths paths)
        => TryLoadModelSelection(paths, out var models, out _) ? models : null;

    /// <summary>
    /// Attempts to load the model selection. Returns false when the config file is present but its
    /// Models section cannot be parsed, so the caller can report the corruption rather than silently
    /// treating it as "no models". Returns true (with null <paramref name="models"/>) when no config
    /// file or Models section exists.
    /// </summary>
    internal static bool TryLoadModelSelection(
        NetclawPaths paths,
        out ModelSelection? models,
        out string? error)
    {
        models = null;
        error = null;
        if (!File.Exists(paths.NetclawConfigPath))
            return true;

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(paths.NetclawConfigPath, optional: false, reloadOnChange: false)
            .Build();
        return TryResolveModelSelection(configuration, out models, out error);
    }

    /// <summary>
    /// Same as the <see cref="NetclawPaths"/> overload, for callers that already hold the parsed
    /// <c>netclaw.json</c> and must not read the file a second time.
    /// </summary>
    internal static bool TryLoadModelSelection(
        Dictionary<string, object> config,
        out ModelSelection? models,
        out string? error)
    {
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(config, JsonDefaults.ConfigFile));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return TryResolveModelSelection(configuration, out models, out error);
    }

    private static bool TryResolveModelSelection(
        IConfiguration configuration,
        out ModelSelection? models,
        out string? error)
    {
        models = null;
        error = null;
        try
        {
            if (!configuration.GetSection("Models").Exists())
                return true;

            models = ModelConfigurationResolver.Resolve(configuration).Selection;
            return true;
        }
        catch (ModelConfigurationException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            error = "model configuration could not be parsed.";
            return false;
        }
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
        writer.WriteLine("  --clear-context-window       Remove the context-window override (re-detect from provider)");
        writer.WriteLine("  --input-modalities <list>    Override input modalities, e.g. \"Text, Image\"");
        writer.WriteLine("  --output-modalities <list>   Override output modalities, e.g. \"Text\"");
        writer.WriteLine("  --clear-modalities           Remove modality overrides (fall back to runtime detection)");
        writer.WriteLine("  --max-output-tokens <tokens> Output limit for every call of this model; thinking tokens count inside it");
        writer.WriteLine($"  --clear-max-output-tokens    Remove the limit (default {ModelReference.DefaultMaxOutputTokens}, clamped per call to the model and the context window)");
        writer.WriteLine("  --thinking <level>           Thinking level: off, low, medium, high, or max (the highest level the provider offers)");
        writer.WriteLine("  --clear-thinking             Remove the level (the model's default applies)");
        writer.WriteLine();
        writer.WriteLine("  Overrides live on named model definitions and survive role switches;");
        writer.WriteLine("  discovery never rewrites a definition. Use the set or matching clear flag to change it.");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  netclaw model list");
        writer.WriteLine("  netclaw model discover my-ollama");
        writer.WriteLine("  netclaw model set main my-ollama qwen3:30b --context-window 32768");
        writer.WriteLine("  netclaw model set main my-openai gpt-x --clear-context-window");
        writer.WriteLine("  netclaw model set main my-vllm qwen-vl --input-modalities \"Text, Image\"");
        writer.WriteLine("  netclaw model set main my-ollama qwen3:30b --clear-modalities");
        writer.WriteLine("  netclaw model set main my-anthropic claude-opus-5-5 --max-output-tokens 64000 --thinking high");
        writer.WriteLine();
        writer.WriteLine("  A level must be offered by the provider dialect and by the model, or `set` rejects it and lists");
        writer.WriteLine("  the offered levels. For an openai-compatible provider, `set` detects the dialect of llama.cpp;");
        writer.WriteLine($"  for another server, set Providers:<name>:VendorOptions:ThinkingDialect to {ThinkingDialectMap.ConfigurableDialectList}.");
        writer.WriteLine("  netclaw model clear fallback");
        return 0;
    }
}
