using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Provider;

/// <summary>
/// Handles <c>netclaw provider</c> CLI subcommands: list, add, remove.
/// </summary>
internal static class ProviderCommand
{
    public static Task<int> RunAsync(string[] args, NetclawPaths paths)
    {
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "list" => Task.FromResult(RunList(paths)),
            "add" => Task.FromResult(RunAdd(args, paths)),
            "remove" => Task.FromResult(RunRemove(args, paths)),
            "help" or "-h" or "--help" => Task.FromResult(WriteHelp()),
            _ => Task.FromResult(WriteHelp())
        };
    }

    private static int RunList(NetclawPaths paths)
    {
        var providers = LoadProviders(paths);

        if (providers.Count == 0)
        {
            Console.WriteLine("No providers configured.");
            Console.WriteLine("Run `netclaw provider add` or `netclaw provider` (TUI) to add one.");
            return 0;
        }

        Console.WriteLine($"{"Name",-20} {"Type",-12} {"Auth",-10} {"Endpoint"}");
        foreach (var (name, entry) in providers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{name,-20} {entry.Type,-12} {entry.AuthMethod,-10} {entry.Endpoint}");
        }

        return 0;
    }

    private static int RunAdd(string[] args, NetclawPaths paths)
    {
        // Parse: netclaw provider add <name> <type> [--api-key <key>] [--endpoint <url>]
        if (args.Length < 4)
        {
            Console.WriteLine("Usage: netclaw provider add <name> <type> [--api-key <key>] [--endpoint <url>]");
            Console.WriteLine();
            Console.WriteLine("Types: " + string.Join(", ", ProviderCapabilities.KnownProviderTypes));
            return 1;
        }

        var name = args[2];
        var type = args[3].ToLowerInvariant();

        if (!ProviderCapabilities.KnownProviderTypes.Contains(type))
        {
            Console.WriteLine($"Error: Unknown provider type '{type}'.");
            Console.WriteLine("Known types: " + string.Join(", ", ProviderCapabilities.KnownProviderTypes));
            return 1;
        }

        string? apiKey = null;
        string? endpoint = null;

        for (var i = 4; i < args.Length; i++)
        {
            if (args[i] is "--api-key" && i + 1 < args.Length)
            {
                apiKey = args[++i];
                continue;
            }

            if (args[i] is "--endpoint" && i + 1 < args.Length)
            {
                endpoint = args[++i];
                continue;
            }
        }

        var supportedAuth = ProviderCapabilities.GetSupportedAuthMethods(type);
        var authMethod = AuthMethod.None;

        if (supportedAuth.Contains(AuthMethod.ApiKey) && apiKey is not null)
        {
            authMethod = AuthMethod.ApiKey;
        }
        else if (supportedAuth.Contains(AuthMethod.ApiKey) && apiKey is null
            && !supportedAuth.Contains(AuthMethod.None))
        {
            // OAuth-capable providers without --api-key: guide user to TUI
            if (supportedAuth.Contains(AuthMethod.OAuthDevice))
            {
                WriteProviderGuidance(type);
                Console.WriteLine();
                Console.WriteLine("Tip: Use `netclaw provider` (TUI) for OAuth device flow setup,");
                Console.WriteLine("     or pass --api-key to use an API key instead.");
                return 1;
            }

            // API-key-only provider without key — prompt
            Console.Write($"API key for {type}: ");
            apiKey = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.WriteLine("Error: API key is required.");
                return 1;
            }

            authMethod = AuthMethod.ApiKey;
        }

        endpoint ??= ProviderCapabilities.GetDefaultEndpoint(type);

        // Write to netclaw.json
        var (config, secrets) = ConfigFileHelper.LoadConfigFiles(paths);

        var providers = ConfigFileHelper.GetOrCreateSection(config, "Providers");
        var providerEntry = new Dictionary<string, object>
        {
            ["Type"] = type,
            ["Endpoint"] = endpoint,
            ["AuthMethod"] = authMethod.ToString()
        };
        providers[name] = providerEntry;
        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);

        // Write secret to secrets.json
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var secretProviders = ConfigFileHelper.GetOrCreateSection(secrets, "Providers");
            secretProviders[name] = new Dictionary<string, object>
            {
                ["ApiKey"] = apiKey
            };
            ConfigFileHelper.WriteConfigFile(paths.SecretsPath, secrets);
        }

        Console.WriteLine($"Added provider '{name}' ({type})");
        WriteProviderGuidance(type);
        Console.WriteLine();
        Console.WriteLine("Note: Restart the daemon for changes to take effect.");
        return 0;
    }

    private static int RunRemove(string[] args, NetclawPaths paths)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: netclaw provider remove <name>");
            return 1;
        }

        var name = args[2];

        // Check if any model roles reference this provider
        var referencingRoles = GetReferencingModelRoles(name, paths);
        if (referencingRoles.Count > 0)
        {
            Console.WriteLine($"Error: Cannot remove provider '{name}' — referenced by model role(s): {string.Join(", ", referencingRoles)}");
            Console.WriteLine("Run `netclaw model set` to reassign these roles first, or `netclaw model clear` for optional roles.");
            return 1;
        }

        var (config, secrets) = ConfigFileHelper.LoadConfigFiles(paths);

        var removed = false;
        var providers = ConfigFileHelper.GetSectionOrNull(config, "Providers");
        if (providers?.Remove(name) == true)
        {
            ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);
            removed = true;
        }

        var secretProviders = ConfigFileHelper.GetSectionOrNull(secrets, "Providers");
        if (secretProviders?.Remove(name) == true)
        {
            ConfigFileHelper.WriteConfigFile(paths.SecretsPath, secrets);
            removed = true;
        }

        if (removed)
        {
            Console.WriteLine($"Removed provider '{name}'");
            Console.WriteLine("Note: Restart the daemon for changes to take effect.");
            return 0;
        }

        Console.WriteLine($"Provider '{name}' not found.");
        return 1;
    }

    /// <summary>
    /// Load provider entries from merged config + secrets files.
    /// </summary>
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Load provider entries from merged config + secrets files.
    /// </summary>
    internal static Dictionary<string, ProviderEntry> LoadProviders(NetclawPaths paths)
    {
        var configText = File.Exists(paths.NetclawConfigPath)
            ? File.ReadAllText(paths.NetclawConfigPath) : "{}";
        var secretsText = File.Exists(paths.SecretsPath)
            ? File.ReadAllText(paths.SecretsPath) : "{}";

        using var configDoc = JsonDocument.Parse(configText);
        using var secretsDoc = JsonDocument.Parse(secretsText);

        var result = new Dictionary<string, ProviderEntry>();

        if (configDoc.RootElement.TryGetProperty("Providers", out var configProviders))
        {
            foreach (var prop in configProviders.EnumerateObject())
            {
                var entry = JsonSerializer.Deserialize<ProviderEntry>(prop.Value.GetRawText(), DeserializeOptions)
                    ?? new ProviderEntry();
                result[prop.Name] = entry;
            }
        }

        // Merge secrets on top
        if (secretsDoc.RootElement.TryGetProperty("Providers", out var secretProviders))
        {
            foreach (var prop in secretProviders.EnumerateObject())
            {
                if (!result.TryGetValue(prop.Name, out var entry))
                    continue;

                if (prop.Value.TryGetProperty("ApiKey", out var apiKey))
                    entry.ApiKey = new SensitiveString(apiKey.GetString() ?? "");
            }
        }

        return result;
    }

    /// <summary>
    /// Check which model roles reference the given provider name.
    /// </summary>
    internal static List<string> GetReferencingModelRoles(string providerName, NetclawPaths paths)
    {
        var roles = new List<string>();
        if (!File.Exists(paths.NetclawConfigPath))
            return roles;

        using var doc = JsonDocument.Parse(File.ReadAllText(paths.NetclawConfigPath));
        if (!doc.RootElement.TryGetProperty("Models", out var models))
            return roles;

        foreach (var roleName in new[] { "Main", "Fallback", "Compaction" })
        {
            if (models.TryGetProperty(roleName, out var role) &&
                role.TryGetProperty("Provider", out var provider) &&
                string.Equals(provider.GetString(), providerName, StringComparison.OrdinalIgnoreCase))
            {
                roles.Add(roleName);
            }
        }

        return roles;
    }

    private static void WriteProviderGuidance(string providerType)
    {
        var guidance = providerType switch
        {
            "ollama" => "Ollama runs locally. No authentication required.",
            "openrouter" => "Get your API key at https://openrouter.ai/keys",
            "anthropic" => "Get your API key at https://console.anthropic.com/settings/keys or use `netclaw provider` for OAuth device flow",
            "openai" => "Get your API key at https://platform.openai.com/api-keys or use `netclaw provider` for OAuth device flow",
            _ => null
        };

        if (guidance is not null)
            Console.WriteLine(guidance);
    }

    private static int WriteHelp()
    {
        Console.WriteLine("Usage: netclaw provider <subcommand>");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  list                         List configured providers");
        Console.WriteLine("  add <name> <type> [options]   Add a provider");
        Console.WriteLine("  remove <name>                Remove a provider");
        Console.WriteLine();
        Console.WriteLine("Run `netclaw provider` (no subcommand) for interactive TUI management.");
        Console.WriteLine();
        Console.WriteLine("Options for 'add':");
        Console.WriteLine("  --api-key <key>       API key (or prompted interactively)");
        Console.WriteLine("  --endpoint <url>      Custom endpoint URL");
        Console.WriteLine();
        Console.WriteLine("Provider types: " + string.Join(", ", ProviderCapabilities.KnownProviderTypes));
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  netclaw provider add my-ollama ollama --endpoint http://big-gpu:11434");
        Console.WriteLine("  netclaw provider add my-anthropic anthropic --api-key sk-ant-...");
        Console.WriteLine("  netclaw provider remove my-ollama");
        return 0;
    }
}
