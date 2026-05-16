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
            "set" => RunSet(args, paths, writer),
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

    private static int RunSet(string[] args, NetclawPaths paths, TextWriter writer)
    {
        // Parse: netclaw model set <role> <provider> <model-id> [--context-window <tokens>]
        if (args.Length < 5)
        {
            writer.WriteLine("Usage: netclaw model set <role> <provider> <model-id> [--context-window <tokens>]");
            writer.WriteLine();
            writer.WriteLine("Roles: main, fallback, compaction");
            return 1;
        }

        var role = args[2].ToLowerInvariant();
        var providerName = args[3];
        var modelId = args[4];
        int? contextWindow = null;

        for (var i = 5; i < args.Length; i++)
        {
            if (args[i] is "--context-window" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out var cw) || cw <= 0)
                {
                    writer.WriteLine("Error: --context-window must be a positive integer.");
                    return 1;
                }

                contextWindow = cw;
            }
        }

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
        if (!providers.ContainsKey(providerName))
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

        // Write to config
        var (config, _) = ConfigFileHelper.LoadConfigFiles(paths);
        var modelsSection = ConfigFileHelper.GetOrCreateSection(config, "Models");

        var modelEntry = new Dictionary<string, object>
        {
            ["Provider"] = providerName,
            ["ModelId"] = modelId,
            ["Provenance"] = ModelDiscoverySource.Manual.ToString()
        };

        if (contextWindow.HasValue)
            modelEntry["ContextWindow"] = contextWindow.Value;

        modelsSection[roleKey] = modelEntry;
        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);

        writer.WriteLine($"Set {role} model to {providerName}/{modelId}");
        return 0;
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

        writer.WriteLine($"Discovering models from '{providerName}' ({entry.Type})...");

        var result = await probe.ProbeAsync(
            entry.Type,
            string.IsNullOrWhiteSpace(entry.Endpoint) ? null : entry.Endpoint,
            entry.ApiKey?.Value ?? entry.OAuthAccessToken?.Value,
            CancellationToken.None);

        if (!result.Success)
        {
            writer.WriteLine($"Error: {result.ErrorMessage}");
            return 1;
        }

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
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  netclaw model list");
        writer.WriteLine("  netclaw model discover my-ollama");
        writer.WriteLine("  netclaw model set main my-ollama qwen3:30b --context-window 32768");
        writer.WriteLine("  netclaw model clear fallback");
        return 0;
    }
}
