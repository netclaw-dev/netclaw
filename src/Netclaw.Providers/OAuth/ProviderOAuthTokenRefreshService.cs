// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthTokenRefreshService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Providers.OAuth;

/// <summary>
/// Refreshes persisted OAuth credentials for inference providers.
/// </summary>
public sealed class ProviderOAuthTokenRefreshService(
    NetclawPaths paths,
    DeviceFlowServiceFactory deviceFlowFactory,
    IOperationalNotificationSink? notificationSink = null,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    private readonly IOperationalNotificationSink _notificationSink = notificationSink ?? NullNotificationSink.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SensitiveString> GetValidAccessTokenAsync(
        string providerName,
        ProviderEntry entry,
        OAuthAuth oauth,
        CancellationToken ct = default)
        => await GetAccessTokenAsync(providerName, entry, oauth, forceRefresh: false, expectedAccessToken: null, ct);

    /// <summary>
    /// Refreshes the provider token after a request was rejected with HTTP 401.
    /// If another request already replaced the token while this call waited for the
    /// provider lock, the newer token is reused instead of a second refresh.
    /// </summary>
    public async Task<SensitiveString> ForceRefreshAccessTokenAsync(
        string providerName,
        ProviderEntry entry,
        OAuthAuth oauth,
        string rejectedAccessToken,
        CancellationToken ct = default)
        => await GetAccessTokenAsync(providerName, entry, oauth, forceRefresh: true, expectedAccessToken: rejectedAccessToken, ct);

    private async Task<SensitiveString> GetAccessTokenAsync(
        string providerName,
        ProviderEntry entry,
        OAuthAuth oauth,
        bool forceRefresh,
        string? expectedAccessToken,
        CancellationToken ct)
    {
        var accessToken = entry.OAuthAccessToken.RequireValid(
            $"OAuth access token for provider '{providerName}'");

        if (!forceRefresh && !NeedsRefresh(entry.OAuthTokenExpiry))
            return accessToken;

        // Coalesce concurrent refreshes for the same configured provider so a
        // burst of requests does not spend the same refresh token multiple times.
        var refreshGate = _refreshLocks.GetOrAdd(providerName, _ => new SemaphoreSlim(1, 1));
        await refreshGate.WaitAsync(ct);
        try
        {
            accessToken = entry.OAuthAccessToken.RequireValid(
                $"OAuth access token for provider '{providerName}'");

            // A forced refresh that raced a concurrent refresh already replaced the
            // token, so reuse the newer token instead of refreshing again. A second
            // refresh would spend the now-rotated refresh token and fail.
            if (forceRefresh
                && expectedAccessToken is not null
                && !string.Equals(accessToken.Value, expectedAccessToken, StringComparison.Ordinal))
            {
                return accessToken;
            }

            if (!forceRefresh && !NeedsRefresh(entry.OAuthTokenExpiry))
                return accessToken;

            if (entry.OAuthRefreshToken.IsNullOrEmpty())
            {
                EmitAuthExpired(providerName, "no_refresh_token");
                throw new ProviderOAuthRefreshRequiredException(
                    $"OAuth token for provider '{providerName}' expired with no refresh token. "
                    + $"Re-authenticate '{providerName}' with `netclaw provider`.");
            }

            var service = deviceFlowFactory.GetFor(oauth);
            var result = await service.RefreshTokenAsync(
                oauth.TokenEndpoint.ToString(),
                oauth.ClientId,
                entry.OAuthRefreshToken,
                ct);

            if (result is null)
            {
                EmitAuthExpired(providerName, "invalid_grant");
                throw new ProviderOAuthRefreshRequiredException(
                    $"OAuth refresh token for provider '{providerName}' was rejected. "
                    + $"Re-authenticate '{providerName}' with `netclaw provider`.");
            }

            ApplyRefreshResult(entry, result);
            // A protector is mandatory here. Passing none used to skip encryption, so a
            // refreshed access and refresh token pair landed in secrets.json as plaintext
            // until some later protected write happened to re-encrypt the file.
            OAuthTokenPersistence.PersistTokens(
                paths, providerName, result, SecretsProtection.CreateProtector(paths));
            return entry.OAuthAccessToken!;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private bool NeedsRefresh(DateTimeOffset? expiresAt)
        => expiresAt is not null && expiresAt.Value - RefreshBuffer <= _timeProvider.GetUtcNow();

    private static void ApplyRefreshResult(ProviderEntry entry, OAuthDeviceFlowResult result)
    {
        var existingRefreshToken = entry.OAuthRefreshToken;
        var existingAccountId = entry.OAuthAccountId;

        entry.OAuthAccessToken = result.AccessToken;
        entry.OAuthRefreshToken = result.RefreshToken ?? existingRefreshToken;
        entry.OAuthTokenExpiry = result.ExpiresAt;
        entry.OAuthAccountId = result.AccountId ?? existingAccountId;
    }

    private void EmitAuthExpired(string providerName, string reason)
        => _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "provider.auth.expired",
            AlertType.ProviderAuthExpired,
            $"OAuth credentials for provider '{providerName}' require re-authentication. Run `netclaw provider`, or `netclaw provider add {providerName} <type> --auth oauth-device` to re-authenticate in place.",
            AlertSeverity.Warning,
            source: providerName,
            context: new Dictionary<string, string>
            {
                ["providerName"] = providerName,
                ["reason"] = reason,
            }));
}
