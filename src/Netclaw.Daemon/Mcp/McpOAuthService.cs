using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Orchestrates the OAuth 2.1 Authorization Code + PKCE lifecycle for MCP servers.
/// Handles discovery, PKCE generation, auth flow coordination, token persistence,
/// and automatic token refresh.
/// </summary>
internal sealed class McpOAuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly NetclawPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpOAuthService> _logger;
    private readonly ISecretsProtector? _protector;

    // In-memory caches, loaded from disk at construction
    private readonly ConcurrentDictionary<string, McpOAuthTokenSet> _tokens = new();
    private readonly ConcurrentDictionary<string, McpOAuthServerMetadata> _metadata = new();
    private readonly ConcurrentDictionary<string, McpOAuthPendingFlow> _pendingFlows = new();

    public McpOAuthService(
        HttpClient httpClient,
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<McpOAuthService> logger,
        ISecretsProtector? protector = null)
    {
        _httpClient = httpClient;
        _paths = paths;
        _timeProvider = timeProvider;
        _logger = logger;
        _protector = protector;

        LoadTokensFromDisk();
        LoadMetadataFromDisk();
    }

    // ── Discovery ──────────────────────────────────────────────────────

    /// <summary>
    /// Auto-detect whether an MCP server requires OAuth by probing its
    /// well-known metadata endpoints. Returns null if no OAuth is needed.
    /// </summary>
    public async Task<McpOAuthServerMetadata?> TryDiscoverMetadataAsync(
        string serverName, string serverUrl, CancellationToken ct)
    {
        serverUrl = serverUrl.TrimEnd('/');

        // Check cache (1-hour TTL)
        if (_metadata.TryGetValue(serverName, out var cached)
            && (_timeProvider.GetUtcNow() - cached.CachedAt).TotalHours < 1)
        {
            return cached;
        }

        // 1. Probe the MCP server URL for a 401 with WWW-Authenticate
        string? resourceMetadataUri = null;
        try
        {
            using var probeRequest = new HttpRequestMessage(HttpMethod.Get, serverUrl);
            using var probeResponse = await _httpClient.SendAsync(probeRequest, ct);

            if (probeResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && probeResponse.Headers.WwwAuthenticate.Count > 0)
            {
                foreach (var auth in probeResponse.Headers.WwwAuthenticate)
                {
                    if (auth.Parameter is not null
                        && auth.Parameter.Contains("resource_metadata", StringComparison.Ordinal))
                    {
                        resourceMetadataUri = ExtractQuotedParam(auth.Parameter, "resource_metadata");
                    }
                }
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Probe of {ServerUrl} failed (expected for OAuth servers)", serverUrl);
        }

        // 2. Resolve protected resource metadata
        string? authServerUrl;
        string? resourceIndicator = null;

        if (resourceMetadataUri is not null)
        {
            var resourceMeta = await _httpClient.GetFromJsonAsync<JsonElement>(resourceMetadataUri, ct);
            authServerUrl = resourceMeta.GetProperty("authorization_servers")[0].GetString();
            if (resourceMeta.TryGetProperty("resource", out var resProp))
                resourceIndicator = resProp.GetString();
        }
        else
        {
            // Fallback: try well-known path on the MCP server origin
            var origin = new Uri(serverUrl).GetLeftPart(UriPartial.Authority);
            var wellKnownUrl = $"{origin}/.well-known/oauth-protected-resource";
            try
            {
                var resourceMeta = await _httpClient.GetFromJsonAsync<JsonElement>(wellKnownUrl, ct);
                authServerUrl = resourceMeta.GetProperty("authorization_servers")[0].GetString();
                if (resourceMeta.TryGetProperty("resource", out var resProp))
                    resourceIndicator = resProp.GetString();
            }
            catch
            {
                // No OAuth metadata found — server doesn't require OAuth
                return null;
            }
        }

        if (string.IsNullOrEmpty(authServerUrl))
            return null;

        // 3. Resolve auth server metadata
        var authMetaUrl = $"{authServerUrl.TrimEnd('/')}/.well-known/oauth-authorization-server";
        var authMeta = await _httpClient.GetFromJsonAsync<JsonElement>(authMetaUrl, ct);

        var metadata = new McpOAuthServerMetadata
        {
            McpServerUrl = serverUrl,
            AuthorizationEndpoint = authMeta.GetProperty("authorization_endpoint").GetString()!,
            TokenEndpoint = authMeta.GetProperty("token_endpoint").GetString()!,
            RegistrationEndpoint = authMeta.TryGetProperty("registration_endpoint", out var regProp)
                ? regProp.GetString() : null,
            ResourceIndicator = resourceIndicator ?? serverUrl,
            CachedAt = _timeProvider.GetUtcNow(),
        };

        _metadata[serverName] = metadata;
        PersistMetadata();

        return metadata;
    }

    // ── Client Registration (RFC 7591 DCR) ─────────────────────────────

    /// <summary>
    /// Ensure a client_id is available for this server. Uses the static
    /// <see cref="McpServerEntry.OAuthClientId"/> if set, otherwise attempts
    /// dynamic client registration.
    /// </summary>
    public async Task<string> EnsureClientRegisteredAsync(
        string serverName, McpServerEntry entry, McpOAuthServerMetadata metadata, CancellationToken ct)
    {
        // Static client ID takes precedence
        if (!string.IsNullOrWhiteSpace(entry.OAuthClientId))
        {
            metadata.ClientId = entry.OAuthClientId;
            _metadata[serverName] = metadata;
            PersistMetadata();
            return entry.OAuthClientId;
        }

        // Already registered?
        if (!string.IsNullOrWhiteSpace(metadata.ClientId))
            return metadata.ClientId;

        // Dynamic client registration
        if (string.IsNullOrWhiteSpace(metadata.RegistrationEndpoint))
            throw new InvalidOperationException(
                $"MCP server '{serverName}' requires OAuth but no client_id is configured " +
                "and the auth server doesn't support dynamic client registration. " +
                $"Set OAuthClientId in the MCP server config.");

        var dcrPayload = new
        {
            client_name = "netclaw",
            redirect_uris = new[] { "http://127.0.0.1:5199/api/mcp/oauth/callback" },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        };

        var response = await _httpClient.PostAsJsonAsync(metadata.RegistrationEndpoint, dcrPayload, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var clientId = result.GetProperty("client_id").GetString()
            ?? throw new InvalidOperationException("DCR response missing client_id");

        metadata.ClientId = clientId;
        _metadata[serverName] = metadata;
        PersistMetadata();

        _logger.LogInformation("Registered OAuth client for MCP server '{Name}': {ClientId}", serverName, clientId);
        return clientId;
    }

    // ── Start Authorization Flow ───────────────────────────────────────

    /// <summary>
    /// Generate PKCE parameters, build the authorization URL, and store
    /// the pending flow. Returns the URL the user should open in their browser.
    /// </summary>
    public async Task<(string AuthorizationUrl, string State)> StartAuthorizationFlowAsync(
        string serverName, McpServerEntry entry, CancellationToken ct)
    {
        var metadata = await TryDiscoverMetadataAsync(serverName, entry.Url!, ct)
            ?? throw new InvalidOperationException(
                $"MCP server '{serverName}' does not advertise OAuth metadata. " +
                "No /.well-known/oauth-protected-resource was found.");
        var clientId = await EnsureClientRegisteredAsync(serverName, entry, metadata, ct);

        // PKCE: generate code_verifier and code_challenge
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");

        var redirectUri = "http://127.0.0.1:5199/api/mcp/oauth/callback";

        var queryParams = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };

        if (!string.IsNullOrWhiteSpace(metadata.ResourceIndicator))
            queryParams["resource"] = metadata.ResourceIndicator;

        if (!string.IsNullOrWhiteSpace(entry.OAuthScope))
            queryParams["scope"] = entry.OAuthScope;

        var authUrl = metadata.AuthorizationEndpoint + "?" + string.Join("&",
            queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var pendingFlow = new McpOAuthPendingFlow
        {
            ServerName = serverName,
            CodeVerifier = codeVerifier,
            State = state,
            RedirectUri = redirectUri,
            ClientId = clientId,
            TokenEndpoint = metadata.TokenEndpoint,
            ResourceIndicator = metadata.ResourceIndicator,
            Completion = new TaskCompletionSource<bool>(),
        };

        _pendingFlows[state] = pendingFlow;

        _logger.LogInformation("Started OAuth flow for MCP server '{Name}' (state={State})", serverName, state);
        return (authUrl, state);
    }

    // ── Complete Authorization Flow (callback) ─────────────────────────

    /// <summary>
    /// Exchange the authorization code for tokens. Called when the browser
    /// redirects back to our callback endpoint.
    /// </summary>
    public async Task CompleteAuthorizationAsync(string code, string state, CancellationToken ct)
    {
        if (!_pendingFlows.TryRemove(state, out var flow))
            throw new InvalidOperationException($"Unknown OAuth state: {state}");

        try
        {
            var tokenRequest = new FormUrlEncodedContent(BuildTokenRequestParams(
                "authorization_code", flow.ClientId, flow.RedirectUri,
                flow.ResourceIndicator, code: code, codeVerifier: flow.CodeVerifier));

            var response = await _httpClient.PostAsync(flow.TokenEndpoint, tokenRequest, ct);
            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

            var tokenSet = ParseTokenResponse(tokenResponse, flow.ClientId, flow.ResourceIndicator);
            _tokens[flow.ServerName] = tokenSet;
            PersistTokens();

            flow.Completion.TrySetResult(true);
            _logger.LogInformation("OAuth flow completed for MCP server '{Name}'", flow.ServerName);
        }
        catch (Exception ex)
        {
            flow.Completion.TrySetException(ex);
            throw;
        }
    }

    // ── Token Access ───────────────────────────────────────────────────

    /// <summary>
    /// Get a valid access token for the given MCP server. Refreshes automatically
    /// if the token is expired and a refresh token is available. Returns null
    /// if no token is available or refresh fails.
    /// </summary>
    public async Task<string?> GetValidTokenAsync(
        string serverName, McpServerEntry entry, CancellationToken ct)
    {
        if (!_tokens.TryGetValue(serverName, out var tokenSet))
            return null;

        // If token hasn't expired, use it
        if (tokenSet.ExpiresAt is null || tokenSet.ExpiresAt > _timeProvider.GetUtcNow())
            return tokenSet.AccessToken.Value;

        // Attempt refresh
        if (tokenSet.RefreshToken is null)
        {
            _logger.LogWarning("Access token expired for MCP server '{Name}' with no refresh token", serverName);
            return null;
        }

        return await RefreshTokenAsync(serverName, entry, tokenSet, ct);
    }

    // ── Token Refresh ──────────────────────────────────────────────────

    private async Task<string?> RefreshTokenAsync(
        string serverName, McpServerEntry entry, McpOAuthTokenSet tokenSet, CancellationToken ct)
    {
        if (!_metadata.TryGetValue(serverName, out var metadata))
        {
            _logger.LogWarning("No cached metadata for MCP server '{Name}', cannot refresh token", serverName);
            return null;
        }

        try
        {
            var refreshRequest = new FormUrlEncodedContent(BuildTokenRequestParams(
                "refresh_token", tokenSet.ClientId ?? entry.OAuthClientId ?? "",
                resourceIndicator: metadata.ResourceIndicator,
                refreshToken: tokenSet.RefreshToken!.Value));

            var response = await _httpClient.PostAsync(metadata.TokenEndpoint, refreshRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                if (errorBody.Contains("invalid_grant", StringComparison.Ordinal))
                {
                    _logger.LogWarning("Refresh token rejected for MCP server '{Name}' (invalid_grant). Re-authorization required.", serverName);
                    _tokens.TryRemove(serverName, out _);
                    PersistTokens();
                    return null;
                }

                response.EnsureSuccessStatusCode(); // throw for other errors
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var newTokenSet = ParseTokenResponse(tokenResponse, tokenSet.ClientId, tokenSet.McpServerUrl);

            // Preserve the existing refresh token if the server didn't issue a new one
            newTokenSet.RefreshToken ??= tokenSet.RefreshToken;

            _tokens[serverName] = newTokenSet;
            PersistTokens();

            _logger.LogInformation("Token refreshed for MCP server '{Name}'", serverName);
            return newTokenSet.AccessToken.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh token for MCP server '{Name}'", serverName);
            return null;
        }
    }

    // ── Flow Status (for CLI polling) ──────────────────────────────────

    public McpOAuthFlowStatus GetFlowStatus(string serverName)
    {
        // Check if there's a completed token
        if (_tokens.ContainsKey(serverName))
            return McpOAuthFlowStatus.Completed;

        // Check if there's a pending flow
        foreach (var flow in _pendingFlows.Values)
        {
            if (flow.ServerName == serverName)
                return McpOAuthFlowStatus.Pending;
        }

        return McpOAuthFlowStatus.NotStarted;
    }

    // ── PKCE Helpers ───────────────────────────────────────────────────

    internal static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    internal static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // ── Private Helpers ────────────────────────────────────────────────

    private static Dictionary<string, string> BuildTokenRequestParams(
        string grantType, string? clientId, string? redirectUri = null,
        string? resourceIndicator = null, string? code = null,
        string? codeVerifier = null, string? refreshToken = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["grant_type"] = grantType,
        };

        if (!string.IsNullOrWhiteSpace(clientId))
            parameters["client_id"] = clientId;
        if (!string.IsNullOrWhiteSpace(redirectUri))
            parameters["redirect_uri"] = redirectUri;
        if (!string.IsNullOrWhiteSpace(resourceIndicator))
            parameters["resource"] = resourceIndicator;
        if (!string.IsNullOrWhiteSpace(code))
            parameters["code"] = code;
        if (!string.IsNullOrWhiteSpace(codeVerifier))
            parameters["code_verifier"] = codeVerifier;
        if (!string.IsNullOrWhiteSpace(refreshToken))
            parameters["refresh_token"] = refreshToken;

        return parameters;
    }

    private McpOAuthTokenSet ParseTokenResponse(
        JsonElement response, string? clientId, string? mcpServerUrl)
    {
        var accessToken = response.GetProperty("access_token").GetString()!;
        string? refreshToken = response.TryGetProperty("refresh_token", out var rtProp)
            ? rtProp.GetString() : null;
        int? expiresIn = response.TryGetProperty("expires_in", out var expProp)
            ? expProp.GetInt32() : null;

        return new McpOAuthTokenSet
        {
            AccessToken = new SensitiveString(accessToken),
            RefreshToken = refreshToken is not null ? new SensitiveString(refreshToken) : null,
            ExpiresAt = expiresIn is not null
                ? _timeProvider.GetUtcNow().AddSeconds(expiresIn.Value)
                : null,
            ClientId = clientId,
            McpServerUrl = mcpServerUrl,
        };
    }

    private static string? ExtractQuotedParam(string headerValue, string paramName)
    {
        var key = paramName + "=\"";
        var start = headerValue.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        start += key.Length;
        var end = headerValue.IndexOf('"', start);
        if (end < 0) return null;

        return headerValue[start..end];
    }

    // ── Persistence ────────────────────────────────────────────────────
    // Tokens go into secrets.json under "McpOAuthTokens".
    // Metadata stays in its own file (non-secret, cache only).

    private const string TokensSectionKey = "McpOAuthTokens";

    private void LoadTokensFromDisk()
    {
        if (!File.Exists(_paths.SecretsPath)) return;

        try
        {
            var json = File.ReadAllText(_paths.SecretsPath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(TokensSectionKey, out var section))
                return;

            var tokens = JsonSerializer.Deserialize<Dictionary<string, McpOAuthTokenSet>>(
                section.GetRawText(), JsonOptions);
            if (tokens is not null)
            {
                foreach (var (key, value) in tokens)
                    _tokens[key] = value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load OAuth tokens from {Path}", _paths.SecretsPath);
        }
    }

    private void LoadMetadataFromDisk()
    {
        if (!File.Exists(_paths.McpOAuthMetadataPath)) return;

        try
        {
            var json = File.ReadAllText(_paths.McpOAuthMetadataPath);
            var metadata = JsonSerializer.Deserialize<Dictionary<string, McpOAuthServerMetadata>>(json, JsonOptions);
            if (metadata is not null)
            {
                foreach (var (key, value) in metadata)
                    _metadata[key] = value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load OAuth metadata from {Path}", _paths.McpOAuthMetadataPath);
        }
    }

    private void PersistTokens()
    {
        try
        {
            // Read existing secrets.json, merge in our tokens section, write back
            Dictionary<string, object> secrets;
            if (File.Exists(_paths.SecretsPath))
            {
                var existing = File.ReadAllText(_paths.SecretsPath);
                secrets = JsonSerializer.Deserialize<Dictionary<string, object>>(existing, JsonOptions)
                    ?? new Dictionary<string, object>();
            }
            else
            {
                secrets = new Dictionary<string, object>();
            }

            secrets[TokensSectionKey] = JsonSerializer.SerializeToElement(
                new Dictionary<string, McpOAuthTokenSet>(_tokens), JsonOptions);

            SecretsFileWriter.Write(_paths.SecretsPath, secrets,
                options: JsonOptions, protector: _protector);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist OAuth tokens to {Path}", _paths.SecretsPath);
        }
    }

    private void PersistMetadata()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new Dictionary<string, McpOAuthServerMetadata>(_metadata), JsonOptions);
            File.WriteAllText(_paths.McpOAuthMetadataPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist OAuth metadata to {Path}", _paths.McpOAuthMetadataPath);
        }
    }
}

internal enum McpOAuthFlowStatus
{
    NotStarted,
    Pending,
    Completed,
    Failed,
}

internal sealed class McpOAuthPendingFlow
{
    public required string ServerName { get; init; }
    public required string CodeVerifier { get; init; }
    public required string State { get; init; }
    public required string RedirectUri { get; init; }
    public required string ClientId { get; init; }
    public required string TokenEndpoint { get; init; }
    public string? ResourceIndicator { get; init; }
    public required TaskCompletionSource<bool> Completion { get; init; }
}
