using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Netclaw.Configuration.Providers.Descriptors;

/// <summary>
/// Provider descriptor for OpenAI.
/// </summary>
public sealed class OpenAiDescriptor : IProviderDescriptor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;

    public OpenAiDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "openai";
    public string DisplayName => "OpenAI";
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.OAuthDevice, AuthMethod.ApiKey];
    public string DefaultEndpoint => "https://api.openai.com";
    public string ModelListingPath => "/v1/models";
    public CredentialInputMode CredentialMode => CredentialInputMode.ApiKey;
    public string? ApiKeyGuidanceUrl => "https://platform.openai.com/api-keys";

    public async Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderProbeResult(false, "API key is required for OpenAI.", []);

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
                "Invalid credentials. Double-check your openai API key.",
            HttpStatusCode.Forbidden =>
                "Access denied. Your openai API key may lack model-listing permissions.",
            HttpStatusCode.NotFound =>
                "The openai models API was not found. The service may be down.",
            HttpStatusCode.TooManyRequests =>
                "Rate limited by openai. Wait a moment and try again.",
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                $"The openai service returned {(int)statusCode}. It may be experiencing issues.",
            _ =>
                $"openai returned HTTP {(int)statusCode}."
        };

        return new ProviderProbeResult(false, message, []);
    }
}
