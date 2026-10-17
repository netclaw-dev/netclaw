// -----------------------------------------------------------------------
// <copyright file="ProviderCredentialWriter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;

namespace Netclaw.Cli.Config;

/// <summary>
/// Single entry point for writing provider credentials (OAuth tokens or API keys)
/// to netclaw.json + secrets.json. Used by both InitWizardViewModel and
/// ProviderManagerViewModel to avoid duplicating credential persistence logic.
/// </summary>
/// <remarks>
/// <para>
/// <b>OAuthTokenExpiry</b> goes in <c>oauth-state.json</c> through
/// <see cref="OAuthTokenPersistence"/>. See <see cref="OAuthStateStore"/>.
/// </para>
/// </remarks>
internal static class ProviderCredentialWriter
{
    /// <summary>
    /// Write a provider entry to netclaw.json and persist credentials to secrets.json.
    /// Merges into existing config, preserving other providers and sections.
    /// </summary>
    /// <param name="paths">Netclaw paths for config + secrets files.</param>
    /// <param name="providerName">The user-facing provider name (config key).</param>
    /// <param name="providerType">Provider type key (e.g. "openai", "anthropic").</param>
    /// <param name="authMethod">Authentication method used.</param>
    /// <param name="endpoint">Custom endpoint, or null to use descriptor default.</param>
    /// <param name="oauthResult">OAuth tokens if auth method is OAuth, null otherwise.</param>
    /// <param name="apiKey">API key if auth method is ApiKey, null otherwise.</param>
    /// <param name="registry">Provider registry for looking up default endpoints.</param>
    /// <param name="protector">Secrets protector override. When null, creates one from paths.</param>
    /// <param name="vendorOptions">Provider-owned non-secret options for netclaw.json.</param>
    internal static void WriteProvider(
        NetclawPaths paths,
        string providerName,
        string providerType,
        AuthMethod authMethod,
        string? endpoint,
        OAuthDeviceFlowResult? oauthResult,
        string? apiKey,
        ProviderDescriptorRegistry? registry = null,
        ISecretsProtector? protector = null,
        IReadOnlyDictionary<string, object?>? vendorOptions = null)
    {
        if (authMethod == AuthMethod.ApiKey && string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key authentication requires a nonblank API key.", nameof(apiKey));

        if (authMethod == AuthMethod.None && !string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("No authentication cannot include an API key.", nameof(apiKey));

        paths.EnsureDirectoriesExist();

        // Build provider config entry in netclaw.json
        var (config, secrets) = ConfigFileHelper.LoadConfigFiles(paths);
        var providers = ConfigFileHelper.GetOrCreateSection(config, "Providers");

        // Configuration keys are case-insensitive: "Local" beside "local" fails the next load.
        providerName = ConfigFileHelper.ResolveName(providers.Keys, providerName);

        var providerEntry = new Dictionary<string, object>
        {
            ["Type"] = providerType
        };

        if (authMethod != AuthMethod.None)
            providerEntry["AuthMethod"] = authMethod.ToString();

        // Resolve endpoint: explicit > descriptor default > empty
        if (string.IsNullOrWhiteSpace(endpoint) && registry is not null
            && registry.TryGet(providerType, out var descriptor))
            endpoint = descriptor.DefaultEndpoint;

        if (!string.IsNullOrWhiteSpace(endpoint))
            providerEntry["Endpoint"] = endpoint;

        if (vendorOptions is not null && vendorOptions.Count > 0)
            providerEntry["VendorOptions"] = vendorOptions;

        PreserveRetiredExpiryBackup(paths, providers, providerName);
        providers[providerName] = providerEntry;
        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);

        // Write credentials to secrets.json
        var effectiveProtector = protector ?? SecretsProtection.CreateProtector(paths);

        if (oauthResult is not null)
        {
            OAuthTokenPersistence.PersistTokens(
                paths, providerName, oauthResult, effectiveProtector);
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            OAuthStateStore.Remove(paths, providerName);
            var secretProviders = ConfigFileHelper.GetOrCreateSection(secrets, "Providers");
            secretProviders[providerName] = new Dictionary<string, object>
            {
                ["ApiKey"] = apiKey
            };
            SecretsFileWriter.Write(paths.SecretsPath, secrets,
                options: JsonDefaults.Indented, protector: effectiveProtector);
        }
        else if (authMethod == AuthMethod.None)
        {
            OAuthStateStore.Remove(paths, providerName);
            var secretProviders = ConfigFileHelper.GetSectionOrNull(secrets, "Providers");
            if (secretProviders?.Remove(providerName) == true)
            {
                SecretsFileWriter.Write(paths.SecretsPath, secrets,
                    options: JsonDefaults.Indented, protector: effectiveProtector);
            }
        }
    }

    private static void PreserveRetiredExpiryBackup(
        NetclawPaths paths, Dictionary<string, object> providers, string providerName)
    {
        if (!providers.TryGetValue(providerName, out var existing)
            || ConfigFileHelper.DeserializeSection<Dictionary<string, object>>(existing) is not { } entry
            || !entry.Keys.Any(static key => string.Equals(key, "OAuthTokenExpiry", StringComparison.OrdinalIgnoreCase))
            || !File.Exists(paths.NetclawConfigPath))
        {
            return;
        }

        File.Copy(paths.NetclawConfigPath, DoctorFixService.NextBackupPath(paths.NetclawConfigPath, "retired-oauth-expiry"), overwrite: false);
    }
}
