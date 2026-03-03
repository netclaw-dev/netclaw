using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration.Providers.OAuth;

/// <summary>
/// Configuration for an RFC 8628 device authorization grant flow.
/// </summary>
public sealed record OAuthDeviceFlowConfig(
    string DeviceAuthorizationEndpoint,
    string TokenEndpoint,
    string ClientId,
    string? Scope = null);

/// <summary>
/// Response from the device authorization endpoint (RFC 8628 §3.2).
/// </summary>
public sealed record DeviceAuthorizationResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

/// <summary>
/// Successful token result from the OAuth device flow.
/// </summary>
public sealed record OAuthDeviceFlowResult(
    SensitiveString AccessToken,
    SensitiveString? RefreshToken,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Observable state of the device flow polling loop.
/// </summary>
public enum DeviceFlowState
{
    NotStarted,
    WaitingForUser,
    Polling,
    Succeeded,
    Denied,
    Expired,
    Cancelled,
    Error
}

/// <summary>
/// Generic RFC 8628 device authorization grant implementation.
/// Parameterized by provider endpoints so it can be reused across providers.
/// </summary>
public sealed class OAuthDeviceFlowService
{
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public OAuthDeviceFlowService(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// POST to the device authorization endpoint to get a user code and verification URI.
    /// </summary>
    public async Task<DeviceAuthorizationResponse> StartDeviceAuthorizationAsync(
        OAuthDeviceFlowConfig config, CancellationToken ct = default)
    {
        var content = new FormUrlEncodedContent(BuildDeviceAuthParams(config));
        var response = await _httpClient.PostAsync(config.DeviceAuthorizationEndpoint, content, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DeviceAuthorizationResponse>(ct);
        return result ?? throw new InvalidOperationException("Empty device authorization response.");
    }

    /// <summary>
    /// Poll the token endpoint until the user authorizes, denies, or the code expires.
    /// </summary>
    public async Task<OAuthDeviceFlowResult> PollForTokenAsync(
        OAuthDeviceFlowConfig config,
        DeviceAuthorizationResponse deviceAuth,
        Action<DeviceFlowState>? onStateChanged = null,
        CancellationToken ct = default)
    {
        var interval = Math.Max(deviceAuth.Interval, 1);
        var deadline = _timeProvider.GetUtcNow().AddSeconds(deviceAuth.ExpiresIn);

        onStateChanged?.Invoke(DeviceFlowState.WaitingForUser);

        while (_timeProvider.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();

            await Task.Delay(TimeSpan.FromSeconds(interval), _timeProvider, ct);

            onStateChanged?.Invoke(DeviceFlowState.Polling);

            var tokenParams = new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceAuth.DeviceCode,
                ["client_id"] = config.ClientId
            };

            var response = await _httpClient.PostAsync(
                config.TokenEndpoint,
                new FormUrlEncodedContent(tokenParams),
                ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (response.IsSuccessStatusCode)
            {
                var result = ParseTokenResponse(root);
                onStateChanged?.Invoke(DeviceFlowState.Succeeded);
                return result;
            }

            var error = root.TryGetProperty("error", out var errProp)
                ? errProp.GetString() : null;

            switch (error)
            {
                case "authorization_pending":
                    // Keep polling
                    continue;

                case "slow_down":
                    // RFC 8628 §3.5: increase interval by 5 seconds
                    interval += 5;
                    continue;

                case "access_denied":
                    onStateChanged?.Invoke(DeviceFlowState.Denied);
                    throw new OAuthDeviceFlowDeniedException();

                case "expired_token":
                    onStateChanged?.Invoke(DeviceFlowState.Expired);
                    throw new OAuthDeviceFlowExpiredException();

                default:
                    var description = root.TryGetProperty("error_description", out var descProp)
                        ? descProp.GetString() : error;
                    onStateChanged?.Invoke(DeviceFlowState.Error);
                    throw new InvalidOperationException(
                        $"OAuth device flow error: {description ?? "unknown error"}");
            }
        }

        onStateChanged?.Invoke(DeviceFlowState.Expired);
        throw new OAuthDeviceFlowExpiredException();
    }

    /// <summary>
    /// Exchange a refresh token for a new access token.
    /// Returns null if the refresh token is invalid or revoked.
    /// </summary>
    public async Task<OAuthDeviceFlowResult?> RefreshTokenAsync(
        string tokenEndpoint,
        string clientId,
        string refreshToken,
        CancellationToken ct = default)
    {
        var tokenParams = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken
        };

        var response = await _httpClient.PostAsync(
            tokenEndpoint,
            new FormUrlEncodedContent(tokenParams),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorJson = await response.Content.ReadAsStringAsync(ct);
            using var errorDoc = JsonDocument.Parse(errorJson);
            var error = errorDoc.RootElement.TryGetProperty("error", out var errProp)
                ? errProp.GetString() : null;

            if (error == "invalid_grant")
                return null;

            response.EnsureSuccessStatusCode(); // throw for other errors
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return ParseTokenResponse(doc.RootElement);
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

    private static List<KeyValuePair<string, string>> BuildDeviceAuthParams(OAuthDeviceFlowConfig config)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", config.ClientId)
        };

        if (config.Scope is not null)
            parameters.Add(new("scope", config.Scope));

        return parameters;
    }
}
