using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Providers.OAuth;

/// <summary>
/// Helpers for persisting and loading OAuth tokens to/from secrets.json.
/// Uses <see cref="SecretsFileWriter"/> for encrypted writes and
/// config-binding-compatible field names matching <see cref="ProviderEntry"/>.
/// </summary>
public static class OAuthTokenPersistence
{
    /// <summary>
    /// Persist OAuth tokens to secrets.json for the given provider name.
    /// Merges into existing secrets content, preserving other providers' entries.
    /// </summary>
    public static void PersistTokens(
        NetclawPaths paths,
        string providerName,
        OAuthDeviceFlowResult result,
        ISecretsProtector? protector = null)
    {
        paths.EnsureDirectoriesExist();

        // Load existing secrets as a JSON object tree to preserve other entries
        var existingJson = File.Exists(paths.SecretsPath)
            ? File.ReadAllText(paths.SecretsPath)
            : "{}";

        var root = JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
        var providers = root["Providers"]?.AsObject() ?? new JsonObject();
        root["Providers"] = providers;

        var providerNode = providers[providerName]?.AsObject() ?? new JsonObject();
        providers[providerName] = providerNode;

        providerNode["OAuthAccessToken"] = result.AccessToken.Value;

        if (result.RefreshToken is not null)
            providerNode["OAuthRefreshToken"] = result.RefreshToken.Value;

        // OAuthTokenExpiry is NOT a secret and must NOT go in secrets.json.
        // SecretsFileWriter encrypts the entire file, and encrypted DateTimeOffset
        // values break IConfiguration binding (silently drops the provider entry).
        // Write expiry to netclaw.json instead.
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        SecretsFileWriter.Write(paths.SecretsPath, json, protector);

        if (result.ExpiresAt.HasValue)
        {
            var configJson = File.Exists(paths.NetclawConfigPath)
                ? File.ReadAllText(paths.NetclawConfigPath) : "{}";
            var configRoot = JsonNode.Parse(configJson)?.AsObject() ?? new JsonObject();
            var configProviders = configRoot["Providers"]?.AsObject() ?? new JsonObject();
            configRoot["Providers"] = configProviders;
            var configProvider = configProviders[providerName]?.AsObject() ?? new JsonObject();
            configProviders[providerName] = configProvider;
            configProvider["OAuthTokenExpiry"] = result.ExpiresAt.Value.ToString("o");
            File.WriteAllText(paths.NetclawConfigPath,
                configRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Load OAuth tokens from secrets.json for the given provider name.
    /// Returns null if no OAuth tokens are stored for this provider.
    /// Transparent decryption happens via <see cref="SensitiveStringTypeConverter"/>.
    /// </summary>
    public static OAuthDeviceFlowResult? LoadTokens(NetclawPaths paths, string providerName)
    {
        if (!File.Exists(paths.SecretsPath))
            return null;

        var json = File.ReadAllText(paths.SecretsPath);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("Providers", out var providers))
            return null;

        if (!providers.TryGetProperty(providerName, out var provider))
            return null;

        if (!provider.TryGetProperty("OAuthAccessToken", out var accessTokenProp))
            return null;

        var accessTokenStr = accessTokenProp.GetString();
        if (string.IsNullOrWhiteSpace(accessTokenStr))
            return null;

        // Transparent decrypt via SensitiveStringTypeConverter.Protector
        var protector = SensitiveStringTypeConverter.Protector;
        if (protector is not null && ISecretsProtector.IsEncrypted(accessTokenStr))
            accessTokenStr = protector.Unprotect(accessTokenStr);

        string? refreshTokenStr = null;
        if (provider.TryGetProperty("OAuthRefreshToken", out var refreshProp))
        {
            refreshTokenStr = refreshProp.GetString();
            if (protector is not null && refreshTokenStr is not null && ISecretsProtector.IsEncrypted(refreshTokenStr))
                refreshTokenStr = protector.Unprotect(refreshTokenStr);
        }

        DateTimeOffset? expiresAt = null;
        if (provider.TryGetProperty("OAuthTokenExpiry", out var expiryProp))
        {
            var expiryStr = expiryProp.GetString();
            if (protector is not null && expiryStr is not null && ISecretsProtector.IsEncrypted(expiryStr))
                expiryStr = protector.Unprotect(expiryStr);

            if (expiryStr is not null && DateTimeOffset.TryParse(expiryStr, out var parsed))
                expiresAt = parsed;
        }

        return new OAuthDeviceFlowResult(
            new SensitiveString(accessTokenStr),
            refreshTokenStr is not null ? new SensitiveString(refreshTokenStr) : null,
            expiresAt);
    }
}
