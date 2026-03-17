using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Netclaw.Configuration.Providers.OAuth;

/// <summary>
/// Shared OAuth Authorization Code + PKCE primitives.
/// Provides PKCE generation, authorization URL construction, token exchange,
/// token refresh, and pending flow state tracking.
/// Used by both provider OAuth and MCP OAuth flows.
/// </summary>
public sealed class OAuthPkceService
{
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, OAuthPkcePendingFlow> _pendingFlows = new();

    public OAuthPkceService(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Generate PKCE parameters, build the authorization URL, and store the pending flow.
    /// Returns the URL the user should open in their browser and the state for tracking.
    /// </summary>
    public (string AuthorizationUrl, string State) StartAuthorizationFlow(
        string authorizationEndpoint,
        string tokenEndpoint,
        string clientId,
        string redirectUri,
        string? scope = null)
    {
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");

        var queryParams = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };

        if (!string.IsNullOrWhiteSpace(scope))
            queryParams["scope"] = scope;

        var authUrl = authorizationEndpoint + "?" + string.Join("&",
            queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var pendingFlow = new OAuthPkcePendingFlow(
            CodeVerifier: codeVerifier,
            State: state,
            RedirectUri: redirectUri,
            ClientId: clientId,
            TokenEndpoint: tokenEndpoint,
            Completion: new TaskCompletionSource<OAuthDeviceFlowResult>());

        _pendingFlows[state] = pendingFlow;

        return (authUrl, state);
    }

    /// <summary>
    /// Exchange the authorization code for tokens. Called when the callback arrives.
    /// </summary>
    public async Task<OAuthDeviceFlowResult> CompleteAuthorizationAsync(
        string code, string state, CancellationToken ct = default)
    {
        if (!_pendingFlows.TryRemove(state, out var flow))
            throw new InvalidOperationException($"Unknown OAuth state: {state}");

        try
        {
            var result = await ExchangeCodeForTokensAsync(
                flow.TokenEndpoint, flow.ClientId, code,
                flow.CodeVerifier, flow.RedirectUri, ct);

            flow.Completion.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            flow.Completion.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Exchange an authorization code for access and refresh tokens via PKCE.
    /// </summary>
    public async Task<OAuthDeviceFlowResult> ExchangeCodeForTokensAsync(
        string tokenEndpoint,
        string clientId,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken ct = default)
    {
        var tokenParams = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        };

        var response = await _httpClient.PostAsync(
            tokenEndpoint,
            new FormUrlEncodedContent(tokenParams),
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return ParseTokenResponse(doc.RootElement);
    }

    /// <summary>
    /// Exchange a refresh token for a new access token.
    /// Returns null if the refresh token is invalid or revoked.
    /// </summary>
    public async Task<OAuthDeviceFlowResult?> RefreshTokenAsync(
        string tokenEndpoint,
        string clientId,
        SensitiveString refreshToken,
        CancellationToken ct = default)
    {
        var tokenParams = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken.Value,
        };

        var response = await _httpClient.PostAsync(
            tokenEndpoint,
            new FormUrlEncodedContent(tokenParams),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            if (errorBody.Contains("invalid_grant", StringComparison.Ordinal))
                return null;

            response.EnsureSuccessStatusCode();
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var result = ParseTokenResponse(doc.RootElement);

        // Preserve existing refresh token if server didn't issue a new one
        result = result with
        {
            RefreshToken = result.RefreshToken ?? refreshToken
        };

        return result;
    }

    /// <summary>
    /// Get the status of a pending flow by state.
    /// </summary>
    public OAuthPkceFlowStatus GetFlowStatus(string state)
    {
        if (!_pendingFlows.TryGetValue(state, out var flow))
            return OAuthPkceFlowStatus.NotStarted;

        return flow.Completion.Task.IsCompletedSuccessfully
            ? OAuthPkceFlowStatus.Completed
            : flow.Completion.Task.IsFaulted
                ? OAuthPkceFlowStatus.Failed
                : OAuthPkceFlowStatus.Pending;
    }

    /// <summary>
    /// Get the result of a completed flow. Returns null if the flow is not complete.
    /// </summary>
    public OAuthDeviceFlowResult? GetFlowResult(string state)
    {
        if (!_pendingFlows.TryGetValue(state, out var flow))
            return null;

        return flow.Completion.Task.IsCompletedSuccessfully
            ? flow.Completion.Task.Result
            : null;
    }

    // ── PKCE Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Generate a cryptographic code_verifier for PKCE (RFC 7636).
    /// </summary>
    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Compute the S256 code_challenge from a code_verifier (RFC 7636).
    /// </summary>
    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// Parse an OAuth token response JSON into <see cref="OAuthDeviceFlowResult"/>.
    /// </summary>
    public static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private OAuthDeviceFlowResult ParseTokenResponse(JsonElement root)
    {
        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Missing access_token in response.");

        string? refreshToken = root.TryGetProperty("refresh_token", out var refreshProp)
            ? refreshProp.GetString() : null;

        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var expiresProp)
            ? _timeProvider.GetUtcNow().AddSeconds(expiresProp.GetInt32())
            : null;

        return new OAuthDeviceFlowResult(
            new SensitiveString(accessToken),
            refreshToken is not null ? new SensitiveString(refreshToken) : null,
            expiresAt);
    }
}

/// <summary>
/// Tracks a pending browser-based OAuth flow.
/// </summary>
public sealed record OAuthPkcePendingFlow(
    string CodeVerifier,
    string State,
    string RedirectUri,
    string ClientId,
    string TokenEndpoint,
    TaskCompletionSource<OAuthDeviceFlowResult> Completion);

/// <summary>
/// Status of a pending OAuth PKCE flow.
/// </summary>
public enum OAuthPkceFlowStatus
{
    NotStarted,
    Pending,
    Completed,
    Failed
}
