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
using System.Threading;
using Netclaw.Configuration;
using Netclaw.Providers.OAuth;

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
public sealed class CopilotTokenExchanger(
    HttpClient httpClient,
    TimeProvider? timeProvider = null,
    ProviderOAuthTokenRefreshService? tokenRefreshService = null)
{
    private const string ComponentName = "copilot-token";

    // Refresh slightly before the server-reported expiry so chat calls in
    // flight when the cache turns over still see a valid bearer token.
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    // One slot per OAuth-token identity. The slot carries both the cached
    // Copilot API token AND the refresh lock — a burst of chat requests
    // arriving inside the 2-minute refresh buffer share one semaphore and
    // therefore fan in to a single call to /copilot_internal/v2/token, not N.
    private readonly ConcurrentDictionary<string, CacheSlot> slots = new();

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

        return await GetTokenAsync(oauthToken, GitHubCopilotAuthResolver.Resolve(entry).CopilotTokenExchangeEndpoint, ct);
    }

    /// <summary>
    /// Returns a valid Copilot API token after first refreshing the persisted
    /// GitHub OAuth credential when its configured expiry is inside the refresh
    /// buffer.
    /// </summary>
    public async Task<string> GetTokenAsync(
        string providerName,
        ProviderEntry entry,
        OAuthAuth oauth,
        CancellationToken ct = default)
    {
        if (tokenRefreshService is null)
            return await GetTokenAsync(entry, ct);

        var oauthToken = await tokenRefreshService.GetValidAccessTokenAsync(
            providerName,
            entry,
            oauth,
            ct);

        return await GetTokenAsync(oauthToken, GitHubCopilotAuthResolver.Resolve(entry).CopilotTokenExchangeEndpoint, ct);
    }

    private async Task<string> GetTokenAsync(SensitiveString oauthToken, Uri tokenEndpoint, CancellationToken ct)
    {
        var slot = slots.GetOrAdd(HashKey(oauthToken.Value, tokenEndpoint), _ => new CacheSlot());

        if (IsFresh(slot.Token))
            return slot.Token!.Token;

        await slot.Lock.WaitAsync(ct);
        try
        {
            // Re-check under the lock — a previous caller may have refreshed
            // while we were waiting.
            if (IsFresh(slot.Token))
                return slot.Token!.Token;

            var fresh = await ExchangeAsync(oauthToken.Value, tokenEndpoint, ct);
            slot.Token = fresh;
            return fresh.Token;
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    private bool IsFresh(CachedToken? cached) =>
        cached is { } c && c.ExpiresAt - RefreshBuffer > time.GetUtcNow();

    private async Task<CachedToken> ExchangeAsync(string oauthToken, Uri tokenEndpoint, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, tokenEndpoint);

        // The exchange endpoint requires the full editor-integration header
        // contract — Copilot-Integration-Id is what tells GitHub's gateway
        // "route through the Copilot permission model, not the generic
        // GitHub App permission model." Without it, even an allowlisted
        // OAuth App's user-to-server token gets evaluated against generic
        // App scopes and rejected with HTTP 403 "Resource not accessible by
        // integration." Cross-checked against CodeAlta's CopilotDirectAuth
        // and the Neovim Copilot plugin source; both send the same set.
        //
        // Stamp the same identity the DI HttpClient handler uses. CLI one-shot
        // paths can construct this exchanger with a raw HttpClient, and GitHub
        // rejects this endpoint outright when User-Agent is absent.
        if (!request.Headers.Contains("User-Agent"))
            request.Headers.TryAddWithoutValidation("User-Agent", NetclawUserAgent.Value);
        if (!request.Headers.Contains(NetclawUserAgent.ComponentHeader))
            request.Headers.TryAddWithoutValidation(NetclawUserAgent.ComponentHeader, ComponentName);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Editor-Version", $"Netclaw/{BuildInfo.Version}");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", $"netclaw/{BuildInfo.Version}");
        request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new CopilotAuthExpiredException();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub Copilot token exchange failed at {tokenEndpoint} with "
                + $"HTTP {(int)response.StatusCode}: {Truncate(body)}");
        }

        var parsed = JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw new InvalidOperationException(
                $"Empty token response from {tokenEndpoint}.");

        // System.Text.Json doesn't enforce required-ness on positional record
        // parameters by default, so a {} response would deserialize to
        // Token=null/ExpiresAt=0 and we'd cache a useless Bearer. Validate
        // here so the failure surfaces at the exchange boundary, not later
        // when the chat call returns 401.
        //
        // We deliberately do NOT include the raw response body in these
        // exception messages — even on validation failure, the body of a
        // 200 response from /copilot_internal/v2/token may still contain a
        // token-shaped string, and exception messages can land in logs or
        // bug reports. The endpoint URL and the missing-field indicator
        // are sufficient for diagnostics.
        if (string.IsNullOrWhiteSpace(parsed.Token))
        {
            throw new InvalidOperationException(
                $"GitHub Copilot token exchange at {tokenEndpoint} returned a "
                + "payload with no 'token' field.");
        }

        if (parsed.ExpiresAt <= 0)
        {
            throw new InvalidOperationException(
                $"GitHub Copilot token exchange at {tokenEndpoint} returned an "
                + $"invalid 'expires_at' value ({parsed.ExpiresAt}).");
        }

        return new CachedToken(
            parsed.Token,
            DateTimeOffset.FromUnixTimeSeconds(parsed.ExpiresAt));
    }

    private static string Truncate(string body) =>
        body.Length > 512 ? body[..512] + "…" : body;

    private static string HashKey(string token, Uri tokenEndpoint)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tokenEndpoint}\n{token}"));
        return Convert.ToHexString(bytes);
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt);

    // Volatile so the lock-free fast-path read in GetTokenAsync sees the
    // store from a previous caller's refresh without acquiring the lock.
    // Reference assignments are atomic in the CLR, but visibility across
    // threads on weak-memory architectures (ARM) requires the barrier.
    private sealed class CacheSlot
    {
        private CachedToken? token;
        public CachedToken? Token
        {
            get => Volatile.Read(ref token);
            set => Volatile.Write(ref token, value);
        }
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expires_at")] long ExpiresAt);
}
