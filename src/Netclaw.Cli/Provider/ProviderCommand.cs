// -----------------------------------------------------------------------
// <copyright file="ProviderCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OAuth;
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
            "list" => Task.FromResult(RunList(paths, registry, writer)),
            "add" => RunAddAsync(args, paths, registry, writer),
            "remove" => Task.FromResult(RunRemove(args, paths, writer)),
            "rename" => Task.FromResult(RunRename(args, paths, writer)),
            "help" or "-h" or "--help" => Task.FromResult(WriteHelp(registry, writer)),
            _ => Task.FromResult(WriteHelp(registry, writer))
        };
    }

    private static int RunRename(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (args.Length < 4)
        {
            writer.WriteLine("Usage: netclaw provider rename <old-name> <new-name>");
            return 1;
        }

        var oldName = args[2];
        var newName = args[3];

        var result = ProviderRenamer.Rename(paths, oldName, newName);
        if (!result.Success)
        {
            writer.WriteLine($"Error: {result.ErrorMessage}");
            return 1;
        }

        writer.WriteLine($"Renamed provider '{oldName}' to '{newName}'.");

        if (result.ReassignedModelRoles.Count > 0)
        {
            writer.WriteLine($"Reassigned model role(s): {string.Join(", ", result.ReassignedModelRoles)}.");
        }

        return 0;
    }

    private static int RunList(NetclawPaths paths, ProviderDescriptorRegistry registry, TextWriter writer)
    {
        var providers = LoadProviders(paths);

        if (providers.Count == 0)
        {
            writer.WriteLine("No providers configured.");
            writer.WriteLine("Run `netclaw provider add` or `netclaw provider` (TUI) to add one.");
            return 0;
        }

        writer.WriteLine($"{"Name",-20} {"Provider",-22} {"Auth",-10} {"Endpoint"}");
        foreach (var (name, entry) in providers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var displayName = registry.TryGet(entry.Type, out var descriptor)
                ? descriptor.DisplayName
                : entry.Type;
            writer.WriteLine($"{name,-20} {displayName,-22} {entry.AuthMethod,-10} {entry.Endpoint}");
        }

        return 0;
    }

    private static async Task<int> RunAddAsync(string[] args, NetclawPaths paths, ProviderDescriptorRegistry registry, TextWriter writer)
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
        string? gitHubHost = null;
        string? gitHubApiBase = null;

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

            if (args[i] is "--github-host" && i + 1 < args.Length)
            {
                gitHubHost = args[++i];
                continue;
            }

            if (args[i] is "--github-api-base" && i + 1 < args.Length)
            {
                gitHubApiBase = args[++i];
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

        var supportedAuth = descriptor.Auth.SupportedAuthMethods;
        if (!TryBuildGitHubCopilotVendorOptions(
                type,
                gitHubHost,
                gitHubApiBase,
                includeAmbientEnvironment: requestedAuthMethod == AuthMethod.OAuthDevice,
                writer,
                out var vendorOptions,
                out var copilotAuthOptions))
        {
            return 1;
        }

        if (ShouldDefaultToOAuthDevice(type, apiKey, requestedAuthMethod, supportedAuth))
            return await RunOAuthDeviceFlowAsync(name, type, endpoint, descriptor, paths, writer, null, null);

        // Handle --auth oauth-device explicitly
        if (requestedAuthMethod == AuthMethod.OAuthDevice)
        {
            if (!supportedAuth.Contains(AuthMethod.OAuthDevice))
            {
                writer.WriteLine($"Error: Provider '{type}' does not support OAuth device flow.");
                return 1;
            }

            return await RunOAuthDeviceFlowAsync(
                name,
                type,
                endpoint,
                descriptor,
                paths,
                writer,
                vendorOptions,
                copilotAuthOptions);
        }

        if (requestedAuthMethod == AuthMethod.ApiKey && !supportedAuth.Contains(AuthMethod.ApiKey))
        {
            writer.WriteLine($"Error: Provider '{type}' does not support API key auth.");
            return 1;
        }

        var forceApiKey = requestedAuthMethod == AuthMethod.ApiKey;
        var authMethod = AuthMethod.None;

        // OAuth-only providers (no API key support, e.g. github-copilot) need
        // --auth oauth-device or the TUI; otherwise we'd silently write an
        // entry with no credentials. Fail loudly per CLAUDE.md.
        if (!supportedAuth.Contains(AuthMethod.ApiKey)
            && !supportedAuth.Contains(AuthMethod.None)
            && supportedAuth.Contains(AuthMethod.OAuthDevice))
        {
            WriteProviderGuidance(descriptor, writer);
            writer.WriteLine();
            writer.WriteLine($"Provider '{type}' requires OAuth device flow.");
            writer.WriteLine("Re-run with --auth oauth-device, or use `netclaw provider` (TUI)");
            writer.WriteLine("for an interactive setup.");
            return 1;
        }

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

        ProviderCredentialWriter.WriteProvider(
            paths, name, type, authMethod, endpoint,
            oauthResult: null, apiKey: apiKey, registry);

        writer.WriteLine($"Added provider '{name}' ({type})");
        WriteProviderGuidance(descriptor, writer);
        writer.WriteLine();
        return 0;
    }

    internal static bool ShouldDefaultToOAuthDevice(
        string providerType,
        string? apiKey,
        AuthMethod? requestedAuthMethod,
        IReadOnlyList<AuthMethod> supportedAuth)
        => requestedAuthMethod is null
           && string.IsNullOrWhiteSpace(apiKey)
           && string.Equals(providerType, "openai", StringComparison.OrdinalIgnoreCase)
           && supportedAuth.Contains(AuthMethod.OAuthDevice);

    internal static bool TryBuildGitHubCopilotVendorOptions(
        string providerType,
        string? gitHubHost,
        string? gitHubApiBase,
        bool includeAmbientEnvironment,
        TextWriter writer,
        out IReadOnlyDictionary<string, object?>? vendorOptions,
        out GitHubCopilotAuthOptions? authOptions)
    {
        vendorOptions = null;
        authOptions = null;
        var hasGitHubCopilotOptions = gitHubHost is not null || gitHubApiBase is not null;
        var isGitHubCopilot = string.Equals(providerType, "github-copilot", StringComparison.OrdinalIgnoreCase);

        if (!isGitHubCopilot)
        {
            if (hasGitHubCopilotOptions)
            {
                writer.WriteLine("Error: GitHub enterprise host options can only be used with provider type 'github-copilot'.");
                return false;
            }

            return true;
        }

        if (!GitHubCopilotAuthResolver.TryResolveSetupOptions(
                gitHubHost,
                gitHubApiBase,
                includeAmbientEnvironment,
                out var resolvedOptions,
                out var error))
        {
            writer.WriteLine($"Error: {error}");
            return false;
        }

        authOptions = resolvedOptions;
        vendorOptions = GitHubCopilotAuthResolver.ToVendorOptions(resolvedOptions);
        return true;
    }

    private static async Task<int> RunOAuthDeviceFlowAsync(
        string name, string type, string? endpoint,
        IProviderDescriptor descriptor,
        NetclawPaths paths,
        TextWriter writer,
        IReadOnlyDictionary<string, object?>? vendorOptions,
        GitHubCopilotAuthOptions? copilotAuthOptions)
    {
        endpoint ??= descriptor.DefaultEndpoint;

        var oauth = string.Equals(type, "github-copilot", StringComparison.OrdinalIgnoreCase)
            ? GitHubCopilotDescriptor.CreateOAuthAuth(copilotAuthOptions ?? new GitHubCopilotAuthOptions())
            : descriptor.Auth.GetOAuthConfig();
        if (oauth is null)
        {
            writer.WriteLine($"Error: Provider '{type}' does not support OAuth.");
            return 1;
        }

        OAuthDeviceFlowConfig config;
        try
        {
            config = OAuthDeviceFlowConfig.FromOAuth(oauth);
        }
        catch (ArgumentException)
        {
            writer.WriteLine($"Error: Provider '{type}' missing OAuth endpoint configuration.");
            return 1;
        }

        using var httpClient = new HttpClient();
        IDeviceFlowService service = oauth.UseProprietaryDeviceFlow
            ? new OpenAiDeviceFlowService(httpClient)
            : new OAuthDeviceFlowService(httpClient);

        try
        {
            // Start device authorization
            writer.WriteLine("Starting OAuth device authorization...");
            var deviceAuth = await service.StartDeviceAuthorizationAsync(config);

            writer.WriteLine();
            writer.WriteLine($"  Visit:      {deviceAuth.VerificationUri}");
            writer.WriteLine($"  Enter code: {deviceAuth.UserCode}");
            writer.WriteLine();
            writer.Write("Waiting for authorization...");

            // Poll for token
            var result = await service.PollForTokenAsync(config, deviceAuth,
                state =>
                {
                    if (state == DeviceFlowState.Polling)
                        writer.Write(".");
                });

            writer.WriteLine();
            writer.WriteLine("Authorization successful!");

            ProviderCredentialWriter.WriteProvider(
                paths, name, type, AuthMethod.OAuthDevice, endpoint,
                oauthResult: result,
                apiKey: null,
                vendorOptions: vendorOptions);

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
                var entry = JsonSerializer.Deserialize<ProviderEntry>(prop.Value.GetRawText(), JsonDefaults.EnumAware)
                    ?? new ProviderEntry();
                if (prop.Value.TryGetProperty(nameof(ProviderEntry.VendorOptions), out var vendorOptions)
                    && vendorOptions.ValueKind == JsonValueKind.Object)
                {
                    entry.SetVendorOptions(JsonNode.Parse(vendorOptions.GetRawText())?.AsObject());
                }

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

                if (prop.Value.TryGetProperty("OAuthAccountId", out var accountId))
                {
                    var decrypted = ConfigFileHelper.DecryptIfEncrypted(paths, accountId.GetString());
                    entry.OAuthAccountId = new SensitiveString(decrypted);
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
        => GetReferencingModelRoleEntries(providerName, paths).Select(e => e.Role).ToList();

    /// <summary>
    /// Like <see cref="GetReferencingModelRoles"/> but also returns each role's current
    /// <c>ModelId</c> so callers can build a fully copy-pasteable
    /// <c>netclaw model set</c> command in their guidance output.
    /// </summary>
    internal static List<(string Role, string ModelId)> GetReferencingModelRoleEntries(
        string providerName, NetclawPaths paths)
    {
        var entries = new List<(string, string)>();
        if (!File.Exists(paths.NetclawConfigPath))
            return entries;

        using var doc = JsonDocument.Parse(File.ReadAllText(paths.NetclawConfigPath));
        if (!doc.RootElement.TryGetProperty("Models", out var models))
            return entries;

        foreach (var roleName in new[] { "Main", "Fallback", "Compaction" })
        {
            if (models.TryGetProperty(roleName, out var role) &&
                role.TryGetProperty("Provider", out var provider) &&
                string.Equals(provider.GetString(), providerName, StringComparison.OrdinalIgnoreCase))
            {
                var modelId = role.TryGetProperty("ModelId", out var mid)
                    ? mid.GetString() ?? "<model-id>"
                    : "<model-id>";
                entries.Add((roleName, modelId));
            }
        }

        return entries;
    }

    private static void WriteProviderGuidance(IProviderDescriptor descriptor, TextWriter writer)
    {
        if (descriptor.Auth is EndpointOnlyAuth)
        {
            writer.WriteLine($"{descriptor.DisplayName} runs locally. No authentication required.");
            return;
        }

        if (descriptor.Auth is OAuthAuth)
        {
            writer.WriteLine($"{descriptor.DisplayName} uses OAuth. Run `netclaw provider` to authenticate.");
            return;
        }

        if (descriptor.Auth.GetApiKeyGuidanceUrl() is { } guidanceUrl)
        {
            var oauthNote = descriptor.Auth.GetOAuthConfig() is not null
                ? " or use `netclaw provider` for OAuth setup"
                : "";
            writer.WriteLine($"Get your API key at {guidanceUrl}{oauthNote}");
        }
    }

    private static int WriteHelp(ProviderDescriptorRegistry registry, TextWriter writer)
    {
        writer.WriteLine("Usage: netclaw provider <subcommand>");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  list                              List configured providers");
        writer.WriteLine("  add <name> <type> [options]       Add a provider");
        writer.WriteLine("  rename <old-name> <new-name>      Rename a provider (config key only)");
        writer.WriteLine("  remove <name>                     Remove a provider");
        writer.WriteLine();
        writer.WriteLine("Run `netclaw provider` (no subcommand) for interactive TUI management.");
        writer.WriteLine();
        writer.WriteLine("Options for 'add':");
        writer.WriteLine("  --api-key <key>       API key (or prompted interactively)");
        writer.WriteLine("  --endpoint <url>      Custom endpoint URL");
        writer.WriteLine("  --auth <method>       Auth method: api-key, oauth-device");
        writer.WriteLine("  --github-host <url>   GitHub Enterprise auth host for github-copilot");
        writer.WriteLine("  --github-api-base <url> GitHub Enterprise API base for github-copilot");
        writer.WriteLine();
        writer.WriteLine("Provider types: " + string.Join(", ", registry.KnownTypeKeys));
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  netclaw provider add my-ollama ollama --endpoint http://my-gpu-server:11434");
        writer.WriteLine("  netclaw provider add my-anthropic anthropic --api-key sk-ant-...");
        writer.WriteLine("  netclaw provider add my-openai openai --auth oauth-device");
        writer.WriteLine("  netclaw provider add copilot-ghe github-copilot --auth oauth-device --github-host https://example.ghe.com --github-api-base https://api.example.ghe.com");
        writer.WriteLine("  netclaw provider rename my-ollama lab-a100");
        writer.WriteLine("  netclaw provider remove my-ollama");
        return 0;
    }

    /// <summary>
    /// Creates a default registry for use outside of DI (CLI subcommands).
    /// </summary>
    internal static ProviderDescriptorRegistry CreateDefaultRegistry()
    {
        var httpClient = new HttpClient();
        var copilotTokenExchanger = new Netclaw.Providers.GitHubCopilot.CopilotTokenExchanger(httpClient);
        var catalog = ProviderDescriptorCatalog.Create(httpClient, copilotTokenExchanger);
        return new ProviderDescriptorRegistry(catalog.All);
    }
}
