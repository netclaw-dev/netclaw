using System.Net;
using System.Text.Json;

namespace Netclaw.Configuration.Providers;

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
    {
        using var doc = JsonDocument.Parse(json);
        var models = new List<DiscoveredModel>();

        if (doc.RootElement.TryGetProperty("data", out var dataArray))
        {
            foreach (var model in dataArray.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id))
                {
                    models.Add(new DiscoveredModel { ModelId = id.GetString()! });
                }
            }
        }

        return new ProviderProbeResult(true, null, models);
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
                return FailForStatus(response.StatusCode, providerName);

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
    /// </summary>
    public static ProviderProbeResult FailForStatus(HttpStatusCode statusCode, string providerName)
    {
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"Invalid credentials. Double-check your {providerName} API key.",
            HttpStatusCode.Forbidden =>
                $"Access denied. Your {providerName} API key may lack model-listing permissions.",
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
}
