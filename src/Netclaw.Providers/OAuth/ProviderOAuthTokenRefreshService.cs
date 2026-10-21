// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthTokenRefreshService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Providers.OAuth;

/// <summary>
/// Refreshes persisted OAuth credentials for inference providers.
/// </summary>
public sealed class ProviderOAuthTokenRefreshService(
    NetclawPaths paths,
    DeviceFlowServiceFactory deviceFlowFactory,
    ILogger<ProviderOAuthTokenRefreshService> logger,
    IOperationalNotificationSink? notificationSink = null,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    private readonly ILogger<ProviderOAuthTokenRefreshService> _logger = logger;
    private readonly IOperationalNotificationSink _notificationSink = notificationSink ?? NullNotificationSink.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.OrdinalIgnoreCase);

    // Only code that holds the provider's refresh gate reads or writes an entry.
    private readonly ConcurrentDictionary<string, RefreshTokenState> _refreshTokens = new(StringComparer.OrdinalIgnoreCase);

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
            AdoptStoredTokens(providerName, entry);
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

            var presented = entry.OAuthRefreshToken;
            var service = deviceFlowFactory.GetFor(oauth);
            var result = await service.RefreshTokenAsync(
                oauth.TokenEndpoint.ToString(),
                oauth.ClientId,
                presented,
                ct);

            if (result is null)
            {
                EmitAuthExpired(providerName, "invalid_grant");
                throw new ProviderOAuthRefreshRequiredException(
                    $"OAuth refresh token for provider '{providerName}' was rejected. "
                    + $"Re-authenticate '{providerName}' with `netclaw provider`.");
            }

            ApplyRefreshResult(entry, result);
            var state = _refreshTokens[providerName];
            state.Live = entry.OAuthRefreshToken;
            if (!string.Equals(presented.Value, state.Live?.Value, StringComparison.Ordinal))
                state.Spent.Add(presented.Value);
            try
            {
                // A protector is mandatory here. Passing none used to skip encryption, so a
                // refreshed access and refresh token pair landed in secrets.json as plaintext
                // until some later protected write happened to re-encrypt the file. The refresh token
                // is written even when the server did not send one, so the file never keeps a spent token.
                OAuthTokenPersistence.PersistTokens(
                    paths,
                    providerName,
                    result with { RefreshToken = entry.OAuthRefreshToken },
                    SecretsProtection.CreateProtector(paths));
                state.LastStored = entry.OAuthRefreshToken?.Value;
            }
            catch (Exception ex)
            {
                // The live token stays in memory. LastStored stays as it was, so the next refresh does
                // not mistake the spent token in secrets.json for a newer one.
                _logger.LogError(
                    ex,
                    "The OAuth refresh for provider {Provider} worked, but Netclaw could not save the new tokens to {SecretsPath}. "
                    + "The saved file can hold a spent refresh token. Fix the file or its directory. Until then this process uses the in-memory tokens.",
                    providerName,
                    paths.SecretsPath);
                throw;
            }

            return entry.OAuthAccessToken!;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    // Another process, such as `netclaw provider`, can refresh the same provider and save a rotated
    // refresh token. The stored refresh token replaces the live one only when it differs from the last
    // one this process saw in secrets.json. A save can fail, and then secrets.json holds a token that is
    // already spent while this service holds the live one. A server with reuse detection revokes the
    // whole token family when a spent token comes back, so that stored token must never be presented.
    // The live token is kept here, not only in the entry, because a caller can replace its entry with
    // one loaded from disk.
    private void AdoptStoredTokens(string providerName, ProviderEntry entry)
    {
        var state = _refreshTokens.GetOrAdd(providerName, _ => new RefreshTokenState(entry.OAuthRefreshToken));
        var stored = OAuthTokenPersistence.LoadTokens(paths, providerName);
        if (stored?.RefreshToken is { } storedRefresh && state.Spent.Contains(storedRefresh.Value))
        {
            _logger.LogWarning(
                "{SecretsPath} holds a refresh token for provider {Provider} that this process already spent. "
                + "An older copy of the file replaced the current one. Netclaw keeps the in-memory tokens and saves them at the next refresh.",
                paths.SecretsPath,
                providerName);
            stored = null;
        }

        var changed = stored is not null
            && !stored.RefreshToken.IsNullOrEmpty()
            && !string.Equals(stored.RefreshToken.Value, state.LastStored, StringComparison.Ordinal);
        if (changed)
        {
            state.Live = stored!.RefreshToken;
            state.LastStored = stored.RefreshToken!.Value;
        }

        if (!state.Live.IsNullOrEmpty())
            entry.OAuthRefreshToken = state.Live;

        if (stored?.ExpiresAt is { } storedExpiry && (entry.OAuthTokenExpiry is not { } expiry || storedExpiry > expiry))
        {
            entry.OAuthAccessToken = stored.AccessToken;
            entry.OAuthTokenExpiry = storedExpiry;
            entry.OAuthAccountId = stored.AccountId ?? entry.OAuthAccountId;
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

    // LastStored is the refresh token that this process last read from, or wrote to, secrets.json.
    // Live is the refresh token to present next. They differ after a save of refreshed tokens fails.
    // Spent holds each refresh token that this process exchanged for a different one.
    private sealed class RefreshTokenState(SensitiveString? initial)
    {
        public string? LastStored { get; set; } = initial?.Value;

        public SensitiveString? Live { get; set; } = initial;

        public HashSet<string> Spent { get; } = new(StringComparer.Ordinal);
    }
}
