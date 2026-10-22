// -----------------------------------------------------------------------
// <copyright file="OAuthTokenPersistence.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
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
    /// Persist OAuth tokens and their expiry to secrets.json for the given provider name,
    /// in one update. Merges into existing secrets content, preserving other providers' entries.
    /// This method never touches netclaw.json: a netclaw.json write restarts the daemon.
    /// </summary>
    public static void PersistTokens(
        NetclawPaths paths,
        string providerName,
        OAuthDeviceFlowResult result,
        ISecretsProtector protector)
    {
        paths.EnsureDirectoriesExist();

        SecretsFileWriter.Update(paths.SecretsPath, (root, _) =>
        {
            var providers = root["Providers"]?.AsObject() ?? [];
            root["Providers"] = providers;

            var providerNode = providers[providerName]?.AsObject() ?? [];
            providers[providerName] = providerNode;

            providerNode.Remove(nameof(ProviderEntry.ApiKey));
            providerNode[nameof(ProviderEntry.OAuthAccessToken)] = result.AccessToken.Value;

            // Preserve any previously-stored refresh token / account id when the new
            // result omits them. An OAuth response that doesn't echo refresh_token means
            // "keep using the existing one" (RFC 6749 §5.1), and a partial refresh that
            // lacks the ChatGPT account id must not wipe a value the Codex backend still
            // requires.
            if (result.RefreshToken is not null)
                providerNode[nameof(ProviderEntry.OAuthRefreshToken)] = result.RefreshToken.Value;

            if (result.AccountId is not null)
                providerNode[nameof(ProviderEntry.OAuthAccountId)] = result.AccountId.Value;

            // Same write as the tokens, so a reader never sees new tokens with an old expiry.
            // JSON null, not a removed key: secrets.json layers after netclaw.json, so null hides
            // a retired netclaw.json value. SecretsFileWriter.IsPlaintextLeaf keeps this leaf readable.
            providerNode[nameof(ProviderEntry.OAuthTokenExpiry)] =
                result.ExpiresAt?.ToString("o", CultureInfo.InvariantCulture);

            return (root, 0);
        }, protector);
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
            if (refreshTokenStr is not null && ISecretsProtector.IsEncrypted(refreshTokenStr))
                refreshTokenStr = protector.Unprotect(refreshTokenStr);
        }

        string? accountIdStr = null;
        if (provider.TryGetProperty("OAuthAccountId", out var accountIdProp))
        {
            accountIdStr = accountIdProp.GetString();
            if (accountIdStr is not null && ISecretsProtector.IsEncrypted(accountIdStr))
                accountIdStr = protector.Unprotect(accountIdStr);
        }

        DateTimeOffset? expiresAt = null;
        if (provider.TryGetProperty("OAuthTokenExpiry", out var expiryProp))
        {
            // A bad value fails loudly: the daemon binder rejects the same value at startup.
            var location = $"secrets.json Providers:{providerName}:OAuthTokenExpiry";
            if (expiryProp.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException($"{location} must be a string or null, not {expiryProp.ValueKind}.");

            var expiryStr = expiryProp.GetString();
            if (expiryStr is not null && ISecretsProtector.IsEncrypted(expiryStr))
                expiryStr = protector.Unprotect(expiryStr);

            expiresAt = OAuthTokenExpiryStorage.Parse(expiryStr, location);
        }

        return new OAuthDeviceFlowResult(
            new SensitiveString(accessTokenStr),
            refreshTokenStr is not null ? new SensitiveString(refreshTokenStr) : null,
            expiresAt,
            accountIdStr is not null ? new SensitiveString(accountIdStr) : null);
    }
}
