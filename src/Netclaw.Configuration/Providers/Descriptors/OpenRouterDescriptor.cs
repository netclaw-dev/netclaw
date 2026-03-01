using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Netclaw.Configuration.Providers.Descriptors;

/// <summary>
/// Provider descriptor for OpenRouter.
/// </summary>
public sealed class OpenRouterDescriptor : IProviderDescriptor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;

    public OpenRouterDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "openrouter";
    public string DisplayName => "OpenRouter";
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.ApiKey];
    public string DefaultEndpoint => "https://openrouter.ai/api/v1";
    public string ModelListingPath => "/models";
    public CredentialInputMode CredentialMode => CredentialInputMode.ApiKey;
    public string? ApiKeyGuidanceUrl => "https://openrouter.ai/keys";

    public async Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderProbeResult(false, "API key is required for OpenRouter.", []);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(entry.Endpoint)
                ? DefaultEndpoint
                : entry.Endpoint.TrimEnd('/');
            var url = $"{baseUrl}{ModelListingPath}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
                return FailForStatus(response.StatusCode);

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return ProbeHelpers.ParseOpenAiStyleModels(json);
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

    private static ProviderProbeResult FailForStatus(HttpStatusCode statusCode)
    {
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "Invalid credentials. Double-check your openrouter API key.",
            HttpStatusCode.Forbidden =>
                "Access denied. Your openrouter API key may lack model-listing permissions.",
            HttpStatusCode.NotFound =>
                "The openrouter models API was not found. The service may be down.",
            HttpStatusCode.TooManyRequests =>
                "Rate limited by openrouter. Wait a moment and try again.",
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                $"The openrouter service returned {(int)statusCode}. It may be experiencing issues.",
            _ =>
                $"openrouter returned HTTP {(int)statusCode}."
        };

        return new ProviderProbeResult(false, message, []);
    }
}
