using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Providers.OAuth;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Provider;

/// <summary>
/// Handles <c>netclaw provider</c> CLI subcommands: list, add, remove.
/// </summary>
internal static class ProviderCommand
{
    public static Task<int> RunAsync(
        string[] args, NetclawPaths paths,
        ProviderDescriptorRegistry? registry = null,
        TextWriter? output = null)
    {
        registry ??= CreateDefaultRegistry();
        var writer = output ?? Console.Out;
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "list" => Task.FromResult(RunList(paths, writer)),
            "add" => Task.FromResult(RunAdd(args, paths, registry, writer)),
            "remove" => Task.FromResult(RunRemove(args, paths, writer)),
            "help" or "-h" or "--help" => Task.FromResult(WriteHelp(registry, writer)),
            _ => Task.FromResult(WriteHelp(registry, writer))
        };
    }

    private static int RunList(NetclawPaths paths, TextWriter writer)
    {
        var providers = LoadProviders(paths);

        if (providers.Count == 0)
        {
            writer.WriteLine("No providers configured.");
            writer.WriteLine("Run `netclaw provider add` or `netclaw provider` (TUI) to add one.");
            return 0;
        }

        writer.WriteLine($"{"Name",-20} {"Type",-12} {"Auth",-10} {"Endpoint"}");
        foreach (var (name, entry) in providers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine($"{name,-20} {entry.Type,-12} {entry.AuthMethod,-10} {entry.Endpoint}");
        }

        return 0;
    }

    private static int RunAdd(string[] args, NetclawPaths paths, ProviderDescriptorRegistry registry, TextWriter writer)
    {
        // Parse: netclaw provider add <name> <type> [--api-key <key>] [--endpoint <url>] [--auth <method>]
        if (args.Length < 4)
        {
            writer.WriteLine("Usage: netclaw provider add <name> <type> [--api-key <key>] [--endpoint <url>] [--auth <method>]");
            writer.WriteLine();
            writer.WriteLine("Types: " + string.Join(", ", registry.KnownTypeKeys));
            writer.WriteLine("Auth methods: api-key, oauth-device");
            return 1;
        }

        var name = args[2];
        var type = args[3].ToLowerInvariant();

        if (!registry.TryGet(type, out var descriptor))
        {
            writer.WriteLine($"Error: Unknown provider type '{type}'.");
            writer.WriteLine("Known types: " + string.Join(", ", registry.KnownTypeKeys));
            return 1;
        }

        string? apiKey = null;
        string? endpoint = null;
        string? authFlag = null;

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

            if (args[i] is "--auth" && i + 1 < args.Length)
            {
                authFlag = args[++i];
                continue;
            }
        }

        AuthMethod? requestedAuthMethod = null;
        if (authFlag is not null)
        {
            if (string.Equals(authFlag, "oauth-device", StringComparison.OrdinalIgnoreCase))
            {
                requestedAuthMethod = AuthMethod.OAuthDevice;
            }
            else if (string.Equals(authFlag, "api-key", StringComparison.OrdinalIgnoreCase))
            {
                requestedAuthMethod = AuthMethod.ApiKey;
            }
            else
            {
                writer.WriteLine($"Error: Unknown auth method '{authFlag}'.");
                writer.WriteLine("Auth methods: api-key, oauth-device");
                return 1;
            }
        }

        var supportedAuth = descriptor.SupportedAuthMethods;

        // Handle --auth oauth-device explicitly
        if (requestedAuthMethod == AuthMethod.OAuthDevice)
        {
            if (!supportedAuth.Contains(AuthMethod.OAuthDevice))
            {
                writer.WriteLine($"Error: Provider '{type}' does not support OAuth device flow.");
                return 1;
            }

            return RunOAuthDeviceFlow(name, type, endpoint, descriptor, paths, writer);
        }

        if (requestedAuthMethod == AuthMethod.ApiKey && !supportedAuth.Contains(AuthMethod.ApiKey))
        {
            writer.WriteLine($"Error: Provider '{type}' does not support API key auth.");
            return 1;
        }

        var forceApiKey = requestedAuthMethod == AuthMethod.ApiKey;
        var authMethod = AuthMethod.None;

        if (supportedAuth.Contains(AuthMethod.ApiKey) && apiKey is not null)
        {
            authMethod = AuthMethod.ApiKey;
        }
        else if (supportedAuth.Contains(AuthMethod.ApiKey) && apiKey is null
            && !supportedAuth.Contains(AuthMethod.None))
        {
            // OAuth-capable providers without --api-key: guide user to TUI or --auth
            if (supportedAuth.Contains(AuthMethod.OAuthDevice) && !forceApiKey)
            {
                WriteProviderGuidance(descriptor, writer);
                writer.WriteLine();
                writer.WriteLine("Tip: Use `netclaw provider` (TUI) for interactive OAuth setup,");
                writer.WriteLine("     or pass --auth oauth-device for CLI device flow,");
                writer.WriteLine("     or pass --api-key to use an API key instead.");
                return 1;
            }

            // API-key-only provider without key — prompt
            writer.Write($"API key for {type}: ");
            apiKey = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                writer.WriteLine("Error: API key is required.");
                return 1;
            }

            authMethod = AuthMethod.ApiKey;
        }

        endpoint ??= descriptor.DefaultEndpoint;

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

        // Write secret to secrets.json via SecretsFileWriter for encryption-at-rest
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var secretProviders = ConfigFileHelper.GetOrCreateSection(secrets, "Providers");
            secretProviders[name] = new Dictionary<string, object>
            {
                ["ApiKey"] = apiKey
            };
            ConfigFileHelper.WriteSecretsFile(paths, secrets);
        }

        writer.WriteLine($"Added provider '{name}' ({type})");
        WriteProviderGuidance(descriptor, writer);
        writer.WriteLine();
        return 0;
    }

    private static int RunOAuthDeviceFlow(
        string name, string type, string? endpoint,
        IProviderDescriptor descriptor, NetclawPaths paths, TextWriter writer)
    {
        if (descriptor.OAuthDeviceEndpoint is null || descriptor.OAuthTokenEndpoint is null
            || descriptor.OAuthDefaultClientId is null)
        {
            writer.WriteLine($"Error: Provider '{type}' missing OAuth endpoint configuration.");
            return 1;
        }

        endpoint ??= descriptor.DefaultEndpoint;

        var config = new OAuthDeviceFlowConfig(
            descriptor.OAuthDeviceEndpoint,
            descriptor.OAuthTokenEndpoint,
            descriptor.OAuthDefaultClientId);

        using var httpClient = new HttpClient();
        var service = new OAuthDeviceFlowService(httpClient);

        try
        {
            // Start device authorization
            writer.WriteLine("Starting OAuth device authorization...");
            var deviceAuth = service.StartDeviceAuthorizationAsync(config).GetAwaiter().GetResult();

            writer.WriteLine();
            writer.WriteLine($"  Visit:      {deviceAuth.VerificationUri}");
            writer.WriteLine($"  Enter code: {deviceAuth.UserCode}");
            writer.WriteLine();
            writer.Write("Waiting for authorization...");

            // Poll for token
            var result = service.PollForTokenAsync(config, deviceAuth,
                state =>
                {
                    if (state == DeviceFlowState.Polling)
                        writer.Write(".");
                }).GetAwaiter().GetResult();

            writer.WriteLine();
            writer.WriteLine("Authorization successful!");

            // Write config to netclaw.json
            var (configDict, _) = ConfigFileHelper.LoadConfigFiles(paths);
            var providers = ConfigFileHelper.GetOrCreateSection(configDict, "Providers");
            providers[name] = new Dictionary<string, object>
            {
                ["Type"] = type,
                ["Endpoint"] = endpoint,
                ["AuthMethod"] = AuthMethod.OAuthDevice.ToString()
            };
            ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, configDict);

            // Persist tokens to secrets.json with encryption
            OAuthTokenPersistence.PersistTokens(paths, name, result, SecretsProtection.CreateProtector(paths));

            writer.WriteLine($"Added provider '{name}' ({type}) with OAuth authentication.");
            return 0;
        }
        catch (OAuthDeviceFlowDeniedException)
        {
            writer.WriteLine();
            writer.WriteLine("Error: Authorization was denied.");
            return 1;
        }
        catch (OAuthDeviceFlowExpiredException)
        {
            writer.WriteLine();
            writer.WriteLine("Error: Authorization code expired. Please try again.");
            return 1;
        }
        catch (Exception ex)
        {
            writer.WriteLine();
            writer.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int RunRemove(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (args.Length < 3)
        {
            writer.WriteLine("Usage: netclaw provider remove <name>");
            return 1;
        }

        var name = args[2];

        // Check if any model roles reference this provider
        var referencingRoles = GetReferencingModelRoles(name, paths);
        if (referencingRoles.Count > 0)
        {
            writer.WriteLine($"Error: Cannot remove provider '{name}' — referenced by model role(s): {string.Join(", ", referencingRoles)}");
            writer.WriteLine("Run `netclaw model set` to reassign these roles first, or `netclaw model clear` for optional roles.");
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
            ConfigFileHelper.WriteSecretsFile(paths, secrets);
            removed = true;
        }

        if (removed)
        {
            writer.WriteLine($"Removed provider '{name}'");
            return 0;
        }

        writer.WriteLine($"Provider '{name}' not found.");
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
                {
                    var decrypted = ConfigFileHelper.DecryptIfEncrypted(paths, apiKey.GetString());
                    entry.ApiKey = new SensitiveString(decrypted);
                }

                if (prop.Value.TryGetProperty("OAuthAccessToken", out var oauthToken))
                {
                    var decrypted = ConfigFileHelper.DecryptIfEncrypted(paths, oauthToken.GetString());
                    entry.OAuthAccessToken = new SensitiveString(decrypted);
                }

                if (prop.Value.TryGetProperty("OAuthRefreshToken", out var refreshToken))
                {
                    var decrypted = ConfigFileHelper.DecryptIfEncrypted(paths, refreshToken.GetString());
                    entry.OAuthRefreshToken = new SensitiveString(decrypted);
                }

                if (prop.Value.TryGetProperty("OAuthTokenExpiry", out var tokenExpiry))
                {
                    var expiryStr = ConfigFileHelper.DecryptIfEncrypted(paths, tokenExpiry.GetString());
                    if (!string.IsNullOrWhiteSpace(expiryStr) && DateTimeOffset.TryParse(expiryStr, out var parsed))
                        entry.OAuthTokenExpiry = parsed;
                }
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

    private static void WriteProviderGuidance(IProviderDescriptor descriptor, TextWriter writer)
    {
        if (descriptor.CredentialMode == CredentialInputMode.EndpointOnly)
        {
            writer.WriteLine($"{descriptor.DisplayName} runs locally. No authentication required.");
            return;
        }

        if (descriptor.ApiKeyGuidanceUrl is { } url)
        {
            var oauthNote = descriptor.SupportedAuthMethods.Contains(AuthMethod.OAuthDevice)
                ? " or use `netclaw provider` for OAuth device flow"
                : "";
            writer.WriteLine($"Get your API key at {url}{oauthNote}");
        }
    }

    private static int WriteHelp(ProviderDescriptorRegistry registry, TextWriter writer)
    {
        writer.WriteLine("Usage: netclaw provider <subcommand>");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  list                         List configured providers");
        writer.WriteLine("  add <name> <type> [options]   Add a provider");
        writer.WriteLine("  remove <name>                Remove a provider");
        writer.WriteLine();
        writer.WriteLine("Run `netclaw provider` (no subcommand) for interactive TUI management.");
        writer.WriteLine();
        writer.WriteLine("Options for 'add':");
        writer.WriteLine("  --api-key <key>       API key (or prompted interactively)");
        writer.WriteLine("  --endpoint <url>      Custom endpoint URL");
        writer.WriteLine("  --auth <method>       Auth method: api-key, oauth-device");
        writer.WriteLine();
        writer.WriteLine("Provider types: " + string.Join(", ", registry.KnownTypeKeys));
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  netclaw provider add my-ollama ollama --endpoint http://big-gpu:11434");
        writer.WriteLine("  netclaw provider add my-anthropic anthropic --api-key sk-ant-...");
        writer.WriteLine("  netclaw provider add my-openai openai --auth oauth-device");
        writer.WriteLine("  netclaw provider remove my-ollama");
        return 0;
    }

    /// <summary>
    /// Creates a default registry for use outside of DI (CLI subcommands).
    /// </summary>
    internal static ProviderDescriptorRegistry CreateDefaultRegistry()
    {
        var httpClient = new HttpClient();
        var catalog = ProviderDescriptorCatalog.Create(httpClient);
        return new ProviderDescriptorRegistry(catalog.All);
    }
}
