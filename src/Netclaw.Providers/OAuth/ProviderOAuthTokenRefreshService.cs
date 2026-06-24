// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthTokenRefreshService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Netclaw.Configuration;

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
    {
        var accessToken = entry.OAuthAccessToken.RequireValid(
            $"OAuth access token for provider '{providerName}'");

        if (!NeedsRefresh(entry.OAuthTokenExpiry))
            return accessToken;

        // Coalesce concurrent refreshes for the same configured provider so a
        // burst of requests does not spend the same refresh token multiple times.
        var refreshGate = _refreshLocks.GetOrAdd(providerName, _ => new SemaphoreSlim(1, 1));
        await refreshGate.WaitAsync(ct);
        try
        {
            accessToken = entry.OAuthAccessToken.RequireValid(
                $"OAuth access token for provider '{providerName}'");

            if (!NeedsRefresh(entry.OAuthTokenExpiry))
                return accessToken;

            if (entry.OAuthRefreshToken.IsNullOrEmpty())
            {
                EmitAuthExpired(providerName, "no_refresh_token");
                throw new ProviderOAuthRefreshRequiredException(
                    $"OAuth token for provider '{providerName}' expired with no refresh token. "
                    + $"Re-authenticate with 'netclaw provider fix {providerName}'.");
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
                    + $"Re-authenticate with 'netclaw provider fix {providerName}'.");
            }

            ApplyRefreshResult(entry, result);
            OAuthTokenPersistence.PersistTokens(paths, providerName, result);
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
            $"OAuth credentials for provider '{providerName}' require re-authentication. Run: netclaw provider fix {providerName}",
            AlertSeverity.Warning,
            source: providerName,
            context: new Dictionary<string, string>
            {
                ["providerName"] = providerName,
                ["reason"] = reason,
            }));
}
