// -----------------------------------------------------------------------
// <copyright file="OAuthTokenPersistence.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        ISecretsProtector protector)
    {
        paths.EnsureDirectoriesExist();

        // Load existing secrets as a JSON object tree to preserve other entries
        var existingJson = File.Exists(paths.SecretsPath)
            ? File.ReadAllText(paths.SecretsPath)
            : "{}";

        var root = JsonNode.Parse(existingJson)?.AsObject() ?? [];
        var providers = root["Providers"]?.AsObject() ?? [];
        root["Providers"] = providers;

        var providerNode = providers[providerName]?.AsObject() ?? [];
        providers[providerName] = providerNode;

        providerNode["OAuthAccessToken"] = result.AccessToken.Value;

        // Preserve any previously-stored refresh token / account id when the new
        // result omits them. An OAuth response that doesn't echo refresh_token means
        // "keep using the existing one" (RFC 6749 §5.1), and a partial refresh that
        // lacks the ChatGPT account id must not wipe a value the Codex backend still
        // requires. (OAuthTokenExpiry below is still cleared on null — a stale expiry
        // is worse than an absent one.)
        if (result.RefreshToken is not null)
            providerNode["OAuthRefreshToken"] = result.RefreshToken.Value;

        if (result.AccountId is not null)
            providerNode["OAuthAccountId"] = result.AccountId.Value;

        // OAuthTokenExpiry is NOT a secret and must NOT go in secrets.json.
        // SecretsFileWriter encrypts the entire file, and encrypted DateTimeOffset
        // values break IConfiguration binding (silently drops the provider entry).
        // Write expiry to netclaw.json instead.
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        SecretsFileWriter.Write(paths.SecretsPath, json, protector);

        PersistTokenExpiry(paths, providerName, result.ExpiresAt);
    }

    private static void PersistTokenExpiry(NetclawPaths paths, string providerName, DateTimeOffset? expiresAt)
    {
        // Never CREATE netclaw.json here: an instance configured purely via
        // NETCLAW_ environment variables has no config file by design, and a
        // token refresh silently materializing one turns the deployment
        // stateful (and throws on a read-only home). Expiry is refresh-timing
        // metadata, not required state — when there is no file to update, skip.
        if (!File.Exists(paths.NetclawConfigPath))
            return;

        var configJson = File.ReadAllText(paths.NetclawConfigPath);
        var configRoot = JsonNode.Parse(configJson)?.AsObject() ?? [];
        var configProviders = configRoot["Providers"]?.AsObject();

        if (configProviders is null)
        {
            if (!expiresAt.HasValue)
                return;

            configProviders = [];
            configRoot["Providers"] = configProviders;
        }

        var configProvider = configProviders[providerName]?.AsObject();
        if (configProvider is null)
        {
            if (!expiresAt.HasValue)
                return;

            configProvider = [];
            configProviders[providerName] = configProvider;
        }

        if (expiresAt.HasValue)
            configProvider["OAuthTokenExpiry"] = expiresAt.Value.ToString("o");
        else
            configProvider.Remove("OAuthTokenExpiry");

        // Atomic write (temp + rename) so a crash/power-loss between truncate and write cannot leave
        // netclaw.json empty or partial — IConfiguration silently drops every section on a torn read.
        // Matches the AtomicFile seam ConfigFileHelper.WriteConfigFile uses for the same file.
        AtomicFile.WriteAllText(paths.NetclawConfigPath,
            configRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

        // Decrypt with the protector for this config's keys directory rather than the process-wide
        // SensitiveStringTypeConverter.Protector static (an ambient hook reserved for the
        // framework-instantiated converters, not a general service locator).
        var protector = SecretsProtection.CreateProtector(paths);
        if (ISecretsProtector.IsEncrypted(accessTokenStr))
            accessTokenStr = protector.Unprotect(accessTokenStr);

        string? refreshTokenStr = null;
        if (provider.TryGetProperty("OAuthRefreshToken", out var refreshProp))
        {
            refreshTokenStr = refreshProp.GetString();
            if (protector is not null && refreshTokenStr is not null && ISecretsProtector.IsEncrypted(refreshTokenStr))
                refreshTokenStr = protector.Unprotect(refreshTokenStr);
        }

        string? accountIdStr = null;
        if (provider.TryGetProperty("OAuthAccountId", out var accountIdProp))
        {
            accountIdStr = accountIdProp.GetString();
            if (protector is not null && accountIdStr is not null && ISecretsProtector.IsEncrypted(accountIdStr))
                accountIdStr = protector.Unprotect(accountIdStr);
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
            expiresAt,
            accountIdStr is not null ? new SensitiveString(accountIdStr) : null);
    }
}
