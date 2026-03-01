using System.Net;
using System.Text.Json;

namespace Netclaw.Configuration.Providers.Descriptors;

/// <summary>
/// Provider descriptor for Anthropic.
/// </summary>
public sealed class AnthropicDescriptor : IProviderDescriptor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;

    public AnthropicDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "anthropic";
    public string DisplayName => "Anthropic";
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.OAuthDevice, AuthMethod.ApiKey];
    public string DefaultEndpoint => "https://api.anthropic.com";
    public string ModelListingPath => "/v1/models";
    public CredentialInputMode CredentialMode => CredentialInputMode.ApiKey;
    public string? ApiKeyGuidanceUrl => "https://console.anthropic.com/settings/keys";

    public async Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderProbeResult(false, "API key is required for Anthropic.", []);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(entry.Endpoint)
                ? DefaultEndpoint
                : entry.Endpoint.TrimEnd('/');
            var url = $"{baseUrl}{ModelListingPath}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

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
                "Invalid credentials. Double-check your anthropic API key.",
            HttpStatusCode.Forbidden =>
                "Access denied. Your anthropic API key may lack model-listing permissions.",
            HttpStatusCode.NotFound =>
                "The anthropic models API was not found. The service may be down.",
            HttpStatusCode.TooManyRequests =>
                "Rate limited by anthropic. Wait a moment and try again.",
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                $"The anthropic service returned {(int)statusCode}. It may be experiencing issues.",
            _ =>
                $"anthropic returned HTTP {(int)statusCode}."
        };

        return new ProviderProbeResult(false, message, []);
    }
}
