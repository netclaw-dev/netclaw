// -----------------------------------------------------------------------
// <copyright file="ProbeHelpers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers;

/// <summary>
/// Shared parsing and execution helpers for provider probe responses.
/// </summary>
internal static class ProbeHelpers
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Parses the OpenAI-style model listing response (used by OpenRouter, Anthropic, OpenAI).
    /// Expects: { "data": [ { "id": "model-id" }, ... ] }
    /// </summary>
    public static ProviderProbeResult ParseOpenAiStyleModels(string json)
        => ParseOpenAiStyleModels(json, _ => null);

    internal static ProviderProbeResult ParseOpenAiStyleModels(
        string json, Func<JsonElement, int?> readContextWindow)
    {
        using var doc = JsonDocument.Parse(json);
        var models = new List<DiscoveredModel>();

        if (doc.RootElement.TryGetProperty("data", out var dataArray))
        {
            foreach (var model in dataArray.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id))
                {
                    models.Add(new DiscoveredModel
                    {
                        ModelId = new(id.GetString()!),
                        ContextWindowTokens = readContextWindow(model)
                    });
                }
            }
        }

        return new ProviderProbeResult(true, null, models);
    }

    internal static int? TryReadInt32(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out var value)
            ? value
            : null;
    }

    internal static int? TryReadPositiveInt32(JsonElement element, string propertyName)
    {
        // Context-window metadata uses 0 as an unset sentinel in some provider APIs
        // (notably llama.cpp router-mode /props). Runtime context windows must be
        // positive, so callers parsing context limits should treat non-positive
        // values as unknown and let later detection/defaulting fill the value.
        return TryReadInt32(element, propertyName) is > 0 and var value
            ? value
            : null;
    }

    internal static int? TryReadPositiveInt32OrFallbackWhenMissing(
        JsonElement element, string propertyName, string fallbackPropertyName)
    {
        // n_ctx is the effective runtime context. If a provider reports it as 0,
        // it is explicitly unknown; do not promote n_ctx_train as if it were the
        // runtime limit, because that can overstate the configured capacity.
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out _))
            return TryReadPositiveInt32(element, propertyName);

        return TryReadPositiveInt32(element, fallbackPropertyName);
    }

    /// <summary>
    /// Common probe execution: builds URL, sends request with timeout,
    /// handles errors, and delegates parsing to the caller.
    /// </summary>
    public static async Task<ProviderProbeResult> ExecuteProbeAsync(
        HttpClient httpClient,
        string providerName,
        string defaultEndpoint,
        string modelListingPath,
        string? entryEndpoint,
        Action<HttpRequestMessage> configureRequest,
        Func<string, ProviderProbeResult> parseResponse,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(entryEndpoint)
                ? defaultEndpoint
                : entryEndpoint.TrimEnd('/');
            var url = $"{baseUrl}{modelListingPath}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            configureRequest(request);

            using var response = await httpClient.SendAsync(request, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var apiErrorDetail = await ExtractApiErrorDetailAsync(response, timeoutCts.Token);
                return FailForStatus(response.StatusCode, providerName, apiErrorDetail);
            }

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return parseResponse(json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProviderProbeResult(false,
                "Connection timed out after 10 seconds. Check that the endpoint is reachable.", []);
        }
        catch (OperationCanceledException)
        {
            return new ProviderProbeResult(false, "Validation cancelled.", []);
        }
        catch (HttpRequestException ex)
        {
            return new ProviderProbeResult(false, $"Connection failed: {ex.Message}", []);
        }
    }

    /// <summary>
    /// Maps HTTP status codes to user-friendly error messages.
    /// Covers auth errors (401/403), rate limiting (429), and server errors (5xx).
    /// When <paramref name="apiErrorDetail"/> is provided, it is appended to auth
    /// error messages so users can see the actual reason from the provider.
    /// </summary>
    public static ProviderProbeResult FailForStatus(
        HttpStatusCode statusCode, string providerName, string? apiErrorDetail = null)
    {
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized => apiErrorDetail is not null
                ? $"Invalid credentials for {providerName}: {apiErrorDetail}"
                : $"Invalid credentials. Double-check your {providerName} credentials.",
            HttpStatusCode.Forbidden => apiErrorDetail is not null
                ? $"Access denied by {providerName}: {apiErrorDetail}"
                : $"Access denied. Your {providerName} credentials may lack model-listing permissions.",
            HttpStatusCode.NotFound =>
                $"The {providerName} models API was not found. The service may be down.",
            HttpStatusCode.TooManyRequests =>
                $"Rate limited by {providerName}. Wait a moment and try again.",
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                $"The {providerName} service returned {(int)statusCode}. It may be experiencing issues.",
            _ =>
                $"{providerName} returned HTTP {(int)statusCode}."
        };

        return new ProviderProbeResult(false, message, []);
    }

    /// <summary>
    /// Best-effort extraction of an error detail string from an HTTP error response body.
    /// Handles OpenAI-style <c>{"error": {"message": "..."}}</c> and simple
    /// <c>{"error": "..."}</c> formats.
    /// </summary>
    internal static async Task<string?> ExtractApiErrorDetailAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var errorProp))
                return null;

            return errorProp.ValueKind switch
            {
                JsonValueKind.Object when errorProp.TryGetProperty("message", out var msgProp) =>
                    msgProp.GetString(),
                JsonValueKind.String => errorProp.GetString(),
                _ => null
            };
        }
        catch
        {
            // Best-effort: malformed body, non-JSON content, etc.
            return null;
        }
    }
}
