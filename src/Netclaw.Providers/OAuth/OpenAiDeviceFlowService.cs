// -----------------------------------------------------------------------
// <copyright file="OpenAiDeviceFlowService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Providers.OAuth;

/// <summary>
/// OpenAI proprietary device authorization flow.
/// Unlike RFC 8628, OpenAI uses a custom 4-step protocol:
/// 1. POST JSON {client_id} to usercode endpoint → {device_auth_id, user_code, interval}
/// 2. User visits verification URL and enters user_code
/// 3. Poll token endpoint with {device_auth_id, user_code} → {authorization_code, code_verifier}
/// 4. PKCE exchange at /oauth/token with authorization_code + code_verifier → access_token
/// </summary>
public sealed class OpenAiDeviceFlowService : IDeviceFlowService
{
    private const string VerificationUri = "https://auth.openai.com/codex/device";
    private const string RedirectUri = "https://auth.openai.com/deviceauth/callback";
    private const int DefaultExpiresIn = 900; // 15 minutes

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public OpenAiDeviceFlowService(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Step 1: POST JSON {client_id} to the usercode endpoint.
    /// Maps OpenAI's response shape to <see cref="DeviceAuthorizationResponse"/>.
    /// </summary>
    public async Task<DeviceAuthorizationResponse> StartDeviceAuthorizationAsync(
        OAuthDeviceFlowConfig config, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string> { ["client_id"] = config.ClientId };
        if (config.Scope is not null)
            payload["scope"] = config.Scope;

        if (config.ExtraAuthParams is not null)
        {
            foreach (var (key, value) in config.ExtraAuthParams)
            {
                if (string.IsNullOrWhiteSpace(key)
                    || key is "client_id" or "scope")
                {
                    continue;
                }

                payload[key] = value;
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(
                config.DeviceAuthorizationEndpoint, payload, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                "Could not reach OpenAI authentication servers. Check your internet connection and try again.",
                ex);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "Device code login is not available for your OpenAI account. " +
                "Try using an API key instead, or contact your workspace admin to enable device code authentication.");
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var result = ParseUserCodeResponse(doc.RootElement);

        var expiresIn = result.ExpiresIn is > 0 ? result.ExpiresIn.Value : DefaultExpiresIn;

        return new DeviceAuthorizationResponse(
            DeviceCode: result.DeviceAuthId,
            UserCode: result.UserCode,
            VerificationUri: VerificationUri,
            ExpiresIn: expiresIn,
            Interval: Math.Max(result.Interval, 1));
    }

    /// <summary>
    /// Steps 3-4: Poll for authorization code, then exchange via PKCE for tokens.
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

            // Step 3: Poll for authorization code
            var pollPayload = new
            {
                device_auth_id = deviceAuth.DeviceCode,
                user_code = deviceAuth.UserCode
            };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsJsonAsync(
                    config.TokenEndpoint, pollPayload, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    "Lost connection to OpenAI authentication servers. Check your internet connection.",
                    ex);
            }

            // 403/404 = authorization pending in OpenAI's proprietary flow.
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                continue;

            // 5xx = transient server error, keep polling (momentary 502/503 shouldn't kill the flow)
            if ((int)response.StatusCode >= 500)
                continue;

            if (!response.IsSuccessStatusCode)
            {
                onStateChanged?.Invoke(DeviceFlowState.Error);
                throw new InvalidOperationException(
                    $"OpenAI returned an unexpected error (HTTP {(int)response.StatusCode}). Please try again.");
            }

            // Success: parse the authorization code + PKCE material
            var authCodeResponse = await response.Content
                .ReadFromJsonAsync<OpenAiAuthCodeResponse>(ct);

            if (authCodeResponse is null)
            {
                onStateChanged?.Invoke(DeviceFlowState.Error);
                throw new InvalidOperationException(
                    "Empty authorization response from OpenAI.");
            }

            // Step 4: Exchange authorization code for tokens via PKCE
            var exchangeEndpoint = config.PkceExchangeEndpoint ?? config.TokenEndpoint;
            var result = await ExchangeCodeForTokensAsync(
                exchangeEndpoint, config.ClientId, authCodeResponse, ct);

            onStateChanged?.Invoke(DeviceFlowState.Succeeded);
            return result;
        }

        onStateChanged?.Invoke(DeviceFlowState.Expired);
        throw new OAuthDeviceFlowExpiredException();
    }

    /// <inheritdoc />
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
            ["refresh_token"] = refreshToken.Value
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

            response.EnsureSuccessStatusCode();
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var result = OAuthTokenResponseParser.Parse(doc.RootElement, _timeProvider);
        return result with
        {
            RefreshToken = result.RefreshToken ?? refreshToken
        };
    }

    public Task<OAuthDeviceFlowResult?> RefreshTokenAsync(
        string tokenEndpoint,
        string clientId,
        string refreshToken,
        CancellationToken ct = default) =>
        RefreshTokenAsync(tokenEndpoint, clientId, new SensitiveString(refreshToken), ct);

    /// <summary>
    /// Step 4: Exchange the authorization code for access/refresh tokens using PKCE.
    /// </summary>
    private async Task<OAuthDeviceFlowResult> ExchangeCodeForTokensAsync(
        string tokenEndpoint,
        string clientId,
        OpenAiAuthCodeResponse authCode,
        CancellationToken ct)
    {
        var tokenParams = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = authCode.AuthorizationCode,
            ["code_verifier"] = authCode.CodeVerifier,
            ["redirect_uri"] = RedirectUri
        };

        var response = await _httpClient.PostAsync(
            tokenEndpoint,
            new FormUrlEncodedContent(tokenParams),
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return OAuthTokenResponseParser.Parse(doc.RootElement, _timeProvider);
    }

    private static OpenAiUserCodeResponse ParseUserCodeResponse(JsonElement root)
    {
        return new OpenAiUserCodeResponse(
            GetRequiredString(root, "device_auth_id"),
            GetRequiredString(root, "user_code", "usercode"),
            GetOptionalInt(root, "interval") ?? 1,
            GetOptionalInt(root, "expires_in"));
    }

    private static string GetRequiredString(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = property.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        throw new InvalidOperationException(
            $"Missing {string.Join("/", propertyNames)} in OpenAI device authorization response.");
    }

    private static int? GetOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numericValue))
            return numericValue;

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringValue))
        {
            return stringValue;
        }

        throw new InvalidOperationException($"Invalid {propertyName} in OpenAI device authorization response.");
    }

    // OpenAI-specific response DTOs

    private sealed record OpenAiUserCodeResponse(
        [property: JsonPropertyName("device_auth_id")] string DeviceAuthId,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("interval")] int Interval,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);

    private sealed record OpenAiAuthCodeResponse(
        [property: JsonPropertyName("authorization_code")] string AuthorizationCode,
        [property: JsonPropertyName("code_verifier")] string CodeVerifier);
}
