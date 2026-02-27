using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Result of probing a provider's model listing API.
/// Validates credentials and discovers available models in one call.
/// </summary>
public sealed record ProviderProbeResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<DiscoveredModel> Models);

/// <summary>
/// Probes a provider's model listing API to validate credentials
/// and discover available models.
/// </summary>
public interface IProviderProbe
{
    Task<ProviderProbeResult> ProbeAsync(
        string providerType, string? endpoint, string? apiKey,
        CancellationToken ct = default);
}

/// <summary>
/// Production implementation of <see cref="IProviderProbe"/> that uses
/// raw HttpClient calls to each provider's model listing API.
/// </summary>
public sealed class ProviderProbe : IProviderProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;

    public ProviderProbe(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ProviderProbeResult> ProbeAsync(
        string providerType, string? endpoint, string? apiKey,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            return providerType.ToLowerInvariant() switch
            {
                "ollama" => await ProbeOllamaAsync(endpoint, timeoutCts.Token),
                "openrouter" => await ProbeOpenRouterAsync(apiKey, timeoutCts.Token),
                "anthropic" => await ProbeAnthropicAsync(apiKey, timeoutCts.Token),
                "openai" => await ProbeOpenAiAsync(apiKey, timeoutCts.Token),
                _ => new ProviderProbeResult(false, $"Unknown provider type: {providerType}", [])
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our timeout fired, not the caller's token
            return new ProviderProbeResult(false,
                "Connection timed out after 10 seconds. Check that the endpoint is reachable.", []);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled (e.g., back navigation)
            return new ProviderProbeResult(false, "Validation cancelled.", []);
        }
        catch (HttpRequestException ex)
        {
            return new ProviderProbeResult(false, $"Connection failed: {ex.Message}", []);
        }
    }

    private async Task<ProviderProbeResult> ProbeOllamaAsync(string? endpoint, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint.TrimEnd('/');
        var url = $"{baseUrl}/api/tags";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            return FailForStatus(response.StatusCode, "ollama", baseUrl);

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);

        var models = new List<DiscoveredModel>();
        if (doc.RootElement.TryGetProperty("models", out var modelsArray))
        {
            foreach (var model in modelsArray.EnumerateArray())
            {
                if (model.TryGetProperty("name", out var name))
                {
                    models.Add(new DiscoveredModel { ModelId = name.GetString()! });
                }
            }
        }

        return new ProviderProbeResult(true, null, models);
    }

    private async Task<ProviderProbeResult> ProbeOpenRouterAsync(string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderProbeResult(false, "API key is required for OpenRouter.", []);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            return FailForStatus(response.StatusCode, "openrouter");

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseOpenAiStyleModels(json);
    }

    private async Task<ProviderProbeResult> ProbeAnthropicAsync(string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderProbeResult(false, "API key is required for Anthropic.", []);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            return FailForStatus(response.StatusCode, "anthropic");

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseOpenAiStyleModels(json);
    }

    private async Task<ProviderProbeResult> ProbeOpenAiAsync(string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderProbeResult(false, "API key is required for OpenAI.", []);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            return FailForStatus(response.StatusCode, "openai");

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseOpenAiStyleModels(json);
    }

    /// <summary>
    /// Produces a human-readable error message from an HTTP status code.
    /// </summary>
    private static ProviderProbeResult FailForStatus(
        HttpStatusCode statusCode, string provider, string? endpoint = null)
    {
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"Invalid credentials. Double-check your {provider} API key.",
            HttpStatusCode.Forbidden =>
                $"Access denied. Your {provider} API key may lack model-listing permissions.",
            HttpStatusCode.NotFound when endpoint is not null =>
                $"Endpoint not found at {endpoint}. Is the service running?",
            HttpStatusCode.NotFound =>
                $"The {provider} models API was not found. The service may be down.",
            HttpStatusCode.TooManyRequests =>
                $"Rate limited by {provider}. Wait a moment and try again.",
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                $"The {provider} service returned {(int)statusCode}. It may be experiencing issues.",
            _ =>
                $"{provider} returned HTTP {(int)statusCode}."
        };

        return new ProviderProbeResult(false, message, []);
    }

    /// <summary>
    /// Parses the OpenAI-style model listing response (used by OpenRouter, Anthropic, OpenAI).
    /// Expects: { "data": [ { "id": "model-id" }, ... ] }
    /// </summary>
    private static ProviderProbeResult ParseOpenAiStyleModels(string json)
    {
        var doc = JsonDocument.Parse(json);
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
}
