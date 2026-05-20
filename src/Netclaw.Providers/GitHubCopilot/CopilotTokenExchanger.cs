// -----------------------------------------------------------------------
// <copyright file="CopilotTokenExchanger.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Providers.GitHubCopilot;

/// <summary>
/// Exchanges a long-lived GitHub OAuth token for a short-lived (~30 min)
/// Copilot API token, caching the result per OAuth-token identity. The cache
/// key is a SHA-256 hash of the OAuth token so multiple Copilot accounts can
/// coexist in one process without colliding, and so token rotation
/// auto-invalidates the cached entry.
/// </summary>
/// <remarks>
/// The short-lived Copilot API token is NEVER persisted to disk. It lives
/// only in this in-memory cache. The long-lived GitHub OAuth token is the
/// only credential that hits the secrets store.
/// </remarks>
public sealed class CopilotTokenExchanger(HttpClient httpClient, TimeProvider? timeProvider = null)
{
    private static readonly Uri TokenEndpoint =
        new("https://api.github.com/copilot_internal/v2/token");

    // Refresh slightly before the server-reported expiry so chat calls in
    // flight when the cache turns over still see a valid bearer token.
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, CachedToken> cache = new();

    // Per-OAuth-token semaphore so a burst of chat requests arriving inside
    // the refresh window doesn't fan out into N concurrent calls to
    // /copilot_internal/v2/token (GitHub rate-limits aggressively). The first
    // caller refreshes; the others wait, then read the new cache entry under
    // the lock.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new();

    /// <summary>
    /// Returns a valid Copilot API token for the OAuth credential carried by
    /// <paramref name="entry"/>, fetching from <c>/copilot_internal/v2/token</c>
    /// when the cached entry is missing or within the 2-minute refresh window.
    /// </summary>
    /// <exception cref="CopilotAuthExpiredException">
    /// The GitHub OAuth token was rejected (HTTP 401).
    /// </exception>
    public async Task<string> GetTokenAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var oauthToken = entry.OAuthAccessToken.RequireValid(
            "GitHub OAuth access token (re-run 'netclaw provider add <name> github-copilot --auth oauth-device')");

        var cacheKey = HashKey(oauthToken.Value);

        if (TryGetFresh(cacheKey, out var cached))
            return cached.Token;

        var sem = refreshLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            // Re-check under the lock — a previous caller may have refreshed
            // while we were waiting.
            if (TryGetFresh(cacheKey, out cached))
                return cached.Token;

            var fresh = await ExchangeAsync(oauthToken.Value, ct);
            cache[cacheKey] = fresh;
            return fresh.Token;
        }
        finally
        {
            sem.Release();
        }
    }

    private bool TryGetFresh(string cacheKey, out CachedToken cached)
    {
        if (cache.TryGetValue(cacheKey, out cached!)
            && cached.ExpiresAt - RefreshBuffer > time.GetUtcNow())
        {
            return true;
        }

        cached = default!;
        return false;
    }

    private async Task<CachedToken> ExchangeAsync(string oauthToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TokenEndpoint);

        // GitHub's /copilot_internal/v2/token expects the OAuth token under
        // the "token" auth scheme (its personal-access-token convention),
        // NOT "Bearer". The Copilot chat endpoint then uses "Bearer" with
        // the short-lived token returned here.
        request.Headers.TryAddWithoutValidation("Authorization", $"token {oauthToken}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("netclaw/1.0");

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new CopilotAuthExpiredException();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub Copilot token exchange failed at {TokenEndpoint} with "
                + $"HTTP {(int)response.StatusCode}: {Truncate(body)}");
        }

        var parsed = JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw new InvalidOperationException(
                $"Empty token response from {TokenEndpoint}.");

        // System.Text.Json doesn't enforce required-ness on positional record
        // parameters by default, so a {} response would deserialize to
        // Token=null/ExpiresAt=0 and we'd cache a useless Bearer. Validate
        // here so the failure surfaces at the exchange boundary, not later
        // when the chat call returns 401.
        if (string.IsNullOrWhiteSpace(parsed.Token))
        {
            throw new InvalidOperationException(
                $"GitHub Copilot token exchange at {TokenEndpoint} returned a "
                + $"payload with no 'token' field: {Truncate(body)}");
        }

        if (parsed.ExpiresAt <= 0)
        {
            throw new InvalidOperationException(
                $"GitHub Copilot token exchange at {TokenEndpoint} returned an "
                + $"invalid 'expires_at' value ({parsed.ExpiresAt}).");
        }

        return new CachedToken(
            parsed.Token,
            DateTimeOffset.FromUnixTimeSeconds(parsed.ExpiresAt));
    }

    private static string Truncate(string body) =>
        body.Length > 512 ? body[..512] + "…" : body;

    private static string HashKey(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expires_at")] long ExpiresAt);
}
