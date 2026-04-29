// -----------------------------------------------------------------------
// <copyright file="ProviderCredentialWriter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
/// <b>OAuthTokenExpiry</b> must go in <c>netclaw.json</c> (not <c>secrets.json</c>)
/// because encrypted <see cref="DateTimeOffset"/> values break <c>IConfiguration</c>
/// binding — the binder silently drops the entire provider entry.
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
    internal static void WriteProvider(
        NetclawPaths paths,
        string providerName,
        string providerType,
        AuthMethod authMethod,
        string? endpoint,
        OAuthDeviceFlowResult? oauthResult,
        string? apiKey,
        ProviderDescriptorRegistry? registry = null,
        ISecretsProtector? protector = null)
    {
        paths.EnsureDirectoriesExist();

        // Build provider config entry in netclaw.json
        var (config, secrets) = ConfigFileHelper.LoadConfigFiles(paths);
        var providers = ConfigFileHelper.GetOrCreateSection(config, "Providers");

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

        // OAuthTokenExpiry goes in netclaw.json (NOT secrets) — see class remarks.
        if (oauthResult?.ExpiresAt is { } expiresAt)
            providerEntry["OAuthTokenExpiry"] = expiresAt.ToString("o");

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
            var secretProviders = ConfigFileHelper.GetOrCreateSection(secrets, "Providers");
            secretProviders[providerName] = new Dictionary<string, object>
            {
                ["ApiKey"] = apiKey
            };
            SecretsFileWriter.Write(paths.SecretsPath, secrets,
                options: JsonDefaults.Indented, protector: effectiveProtector);
        }
    }
}
