using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Cli.Config;
using Netclaw.Cli.Provider;
using Netclaw.Configuration;

namespace Netclaw.Cli.Model;

/// <summary>
/// Handles <c>netclaw model</c> CLI subcommands: list, set, discover, clear.
/// </summary>
internal static class ModelCommand
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public static async Task<int> RunAsync(string[] args, NetclawPaths paths, IProviderProbe? probe = null)
    {
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "list" => RunList(paths),
            "set" => RunSet(args, paths),
            "discover" => await RunDiscoverAsync(args, paths, probe),
            "clear" => RunClear(args, paths),
            "help" or "-h" or "--help" => WriteHelp(),
            _ => WriteHelp()
        };
    }

    private static int RunList(NetclawPaths paths)
    {
        var models = LoadModelSelection(paths);
        if (models is null)
        {
            Console.WriteLine("No models configured.");
            Console.WriteLine("Run `netclaw model set` or `netclaw model` (TUI) to configure models.");
            return 0;
        }

        Console.WriteLine($"{"Role",-12} {"Provider",-20} {"Model ID",-30} {"Context Window"}");

        WriteModelRow("Main", models.Main);

        if (models.Fallback is not null)
            WriteModelRow("Fallback", models.Fallback);
        else
            Console.WriteLine($"{"Fallback",-12} {"(not set)",-20}");

        if (models.Compaction is not null)
            WriteModelRow("Compaction", models.Compaction);
        else
            Console.WriteLine($"{"Compaction",-12} {"(not set)",-20}");

        return 0;
    }

    private static void WriteModelRow(string role, ModelReference model)
    {
        var ctxWindow = model.ContextWindow.HasValue
            ? $"{model.ContextWindow.Value:N0} tokens"
            : "(default)";
        Console.WriteLine($"{role,-12} {model.Provider,-20} {model.ModelId,-30} {ctxWindow}");
    }

    private static int RunSet(string[] args, NetclawPaths paths)
    {
        // Parse: netclaw model set <role> <provider> <model-id> [--context-window <tokens>]
        if (args.Length < 5)
        {
            Console.WriteLine("Usage: netclaw model set <role> <provider> <model-id> [--context-window <tokens>]");
            Console.WriteLine();
            Console.WriteLine("Roles: main, fallback, compaction");
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
                    Console.WriteLine("Error: --context-window must be a positive integer.");
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
            Console.WriteLine($"Error: Unknown role '{role}'. Valid roles: main, fallback, compaction");
            return 1;
        }

        // Validate provider exists
        var providers = ProviderCommand.LoadProviders(paths);
        if (!providers.ContainsKey(providerName))
        {
            Console.WriteLine($"Error: Provider '{providerName}' not found in configuration.");
            Console.WriteLine("Configured providers: " +
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
                Console.WriteLine($"Warning: Context window shrinking from {currentModels.Main.ContextWindow.Value:N0} to {contextWindow.Value:N0} tokens.");
                Console.WriteLine("         Existing sessions with longer history may fail until compacted.");
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

        Console.WriteLine($"Set {role} model to {providerName}/{modelId}");
        Console.WriteLine("Note: Restart the daemon for changes to take effect.");
        return 0;
    }

    private static async Task<int> RunDiscoverAsync(string[] args, NetclawPaths paths, IProviderProbe? probe)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: netclaw model discover <provider>");
            return 1;
        }

        var providerName = args[2];
        var providers = ProviderCommand.LoadProviders(paths);

        if (!providers.TryGetValue(providerName, out var entry))
        {
            Console.WriteLine($"Error: Provider '{providerName}' not found in configuration.");
            return 1;
        }

        probe ??= CreateDefaultProbe();

        Console.WriteLine($"Discovering models from '{providerName}' ({entry.Type})...");

        var result = await probe.ProbeAsync(
            entry.Type,
            string.IsNullOrWhiteSpace(entry.Endpoint) ? null : entry.Endpoint,
            entry.ApiKey?.Value,
            CancellationToken.None);

        if (!result.Success)
        {
            Console.WriteLine($"Error: {result.ErrorMessage}");
            return 1;
        }

        if (result.Models.Count == 0)
        {
            Console.WriteLine("No models found.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"{"Model ID",-40} {"Context Window",-16} {"Cost (in/out per 1M)"}");
        foreach (var model in result.Models.OrderBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase))
        {
            var ctx = model.ContextWindowTokens.HasValue
                ? $"{model.ContextWindowTokens.Value:N0}"
                : "-";
            var cost = (model.CostPerMillionInputTokens, model.CostPerMillionOutputTokens) switch
            {
                (not null, not null) => $"${model.CostPerMillionInputTokens:F2} / ${model.CostPerMillionOutputTokens:F2}",
                _ => "-"
            };
            Console.WriteLine($"{model.ModelId,-40} {ctx,-16} {cost}");
        }

        Console.WriteLine();
        Console.WriteLine($"{result.Models.Count} model(s) found.");
        return 0;
    }

    private static int RunClear(string[] args, NetclawPaths paths)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: netclaw model clear <role>");
            Console.WriteLine();
            Console.WriteLine("Roles: fallback, compaction (cannot clear main)");
            return 1;
        }

        var role = args[2].ToLowerInvariant();

        if (role is "main")
        {
            Console.WriteLine("Error: Cannot clear the main model role. Use `netclaw model set main` to change it instead.");
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
            Console.WriteLine($"Error: Unknown role '{role}'. Valid roles for clear: fallback, compaction");
            return 1;
        }

        var (config, _) = ConfigFileHelper.LoadConfigFiles(paths);
        var modelsSection = ConfigFileHelper.GetSectionOrNull(config, "Models");

        if (modelsSection is null || !modelsSection.ContainsKey(roleKey))
        {
            Console.WriteLine($"Role '{role}' is not configured.");
            return 0;
        }

        modelsSection.Remove(roleKey);
        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);

        Console.WriteLine($"Cleared {role} model role.");
        Console.WriteLine("Note: Restart the daemon for changes to take effect.");
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

        return JsonSerializer.Deserialize<ModelSelection>(modelsElement.GetRawText(), DeserializeOptions);
    }

    private static IProviderProbe CreateDefaultProbe()
    {
        return new Tui.ProviderProbe(new HttpClient());
    }

    private static int WriteHelp()
    {
        Console.WriteLine("Usage: netclaw model <subcommand>");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  list                                     Show current model assignments");
        Console.WriteLine("  set <role> <provider> <model-id>         Assign model to role");
        Console.WriteLine("  discover <provider>                      List available models from provider");
        Console.WriteLine("  clear <role>                             Clear fallback or compaction role");
        Console.WriteLine();
        Console.WriteLine("Run `netclaw model` (no subcommand) for interactive TUI management.");
        Console.WriteLine();
        Console.WriteLine("Roles: main, fallback, compaction");
        Console.WriteLine();
        Console.WriteLine("Options for 'set':");
        Console.WriteLine("  --context-window <tokens>    Override context window size");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  netclaw model list");
        Console.WriteLine("  netclaw model discover my-ollama");
        Console.WriteLine("  netclaw model set main my-ollama qwen3:30b --context-window 32768");
        Console.WriteLine("  netclaw model clear fallback");
        return 0;
    }
}
