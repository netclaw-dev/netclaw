// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthTokenRefreshService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Net;
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
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(2);

    private readonly ILogger<ProviderOAuthTokenRefreshService> _logger = logger;
    private readonly IOperationalNotificationSink _notificationSink = notificationSink ?? NullNotificationSink.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.OrdinalIgnoreCase);

    // Only code that holds the provider's refresh gate reads or writes an entry.
    private readonly ConcurrentDictionary<string, RefreshTokenState> _refreshTokens = new(StringComparer.OrdinalIgnoreCase);

    // A provider is in this map from a rejected refresh until a refresh works or a new sign-in
    // replaces the access token that was in use at the failure.
    private readonly ConcurrentDictionary<string, AuthFailure> _authFailures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The providers whose OAuth credentials need a new sign-in, for the daemon status.</summary>
    public IReadOnlyList<ProviderAuthFailure> GetAuthFailures()
        => _authFailures
            .OrderBy(failure => failure.Key, StringComparer.OrdinalIgnoreCase)
            .Select(failure => new ProviderAuthFailure(failure.Key, failure.Value.Reason))
            .ToList();

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
            return SignedIn(providerName, accessToken);

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
                return SignedIn(providerName, accessToken);
            }

            if (!forceRefresh && !NeedsRefresh(entry.OAuthTokenExpiry))
                return SignedIn(providerName, accessToken);

            if (entry.OAuthRefreshToken.IsNullOrEmpty())
            {
                ReportAuthExpired(providerName, "no_refresh_token", accessToken);
                throw new ProviderOAuthRefreshRequiredException(
                    $"OAuth token for provider '{providerName}' expired with no refresh token. "
                    + $"Re-authenticate '{providerName}' with `netclaw provider`.");
            }

            var presented = entry.OAuthRefreshToken;
            OAuthDeviceFlowResult result;
            try
            {
                result = await RefreshWithOneRetryAsync(deviceFlowFactory.GetFor(oauth), oauth, presented, ct);
            }
            catch (OAuthRefreshTokenRejectedException ex)
            {
                ReportAuthExpired(providerName, ex.Code, accessToken);
                throw new ProviderOAuthRefreshRequiredException(
                    $"OAuth refresh token for provider '{providerName}' was rejected ({ex.Code}). "
                    + $"Re-authenticate '{providerName}' with `netclaw provider`.");
            }

            _authFailures.TryRemove(providerName, out _);
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

    // A transient failure gets one more try after a short pause. A rejected refresh token gets none,
    // and neither does a cancellation by the caller. A timeout with no cancellation by the caller is
    // transient. The server may have rotated the token before the timeout. Then the retry presents
    // a spent token and the server rejects it, which reaches the re-authentication path.
    private async Task<OAuthDeviceFlowResult> RefreshWithOneRetryAsync(
        IDeviceFlowService service,
        OAuthAuth oauth,
        SensitiveString refreshToken,
        CancellationToken ct)
    {
        try
        {
            return await service.RefreshTokenAsync(oauth.TokenEndpoint.ToString(), oauth.ClientId, refreshToken, ct);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            _logger.LogWarning(ex, "The OAuth refresh failed with a transient error. It will retry once in {Delay}.", TransientRetryDelay);
        }

        await Task.Delay(TransientRetryDelay, _timeProvider, ct);
        return await service.RefreshTokenAsync(oauth.TokenEndpoint.ToString(), oauth.ClientId, refreshToken, ct);
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: var status } => (int)status >= 500 || status == HttpStatusCode.TooManyRequests,
        OperationCanceledException => !ct.IsCancellationRequested,
        _ => false,
    };

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

    private SensitiveString SignedIn(string providerName, SensitiveString accessToken)
    {
        if (_authFailures.TryGetValue(providerName, out var failure)
            && !string.Equals(failure.AccessTokenAtFailure.Value, accessToken.Value, StringComparison.Ordinal))
        {
            _authFailures.TryRemove(KeyValuePair.Create(providerName, failure));
        }

        return accessToken;
    }

    private void ReportAuthExpired(string providerName, string reason, SensitiveString accessToken)
    {
        _authFailures[providerName] = new AuthFailure(reason, accessToken);
        _logger.LogError(
            "Provider {Provider} needs a new OAuth sign-in. Reason: {Reason}. Run `netclaw provider` to sign in again.",
            providerName,
            reason);
        _notificationSink.Emit(OperationalAlert.Create(
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

    private sealed record AuthFailure(string Reason, SensitiveString AccessTokenAtFailure);

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

/// <summary>A provider whose OAuth credentials need a new sign-in.</summary>
/// <param name="Provider">The configured provider name.</param>
/// <param name="Reason">The rejection code from the token server, or <c>no_refresh_token</c>.</param>
public sealed record ProviderAuthFailure(string Provider, string Reason);
