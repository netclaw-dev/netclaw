using System.Net;
using System.Text.Json;

namespace Netclaw.Configuration.Providers.Descriptors;

/// <summary>
/// Provider descriptor for Ollama (local inference server).
/// </summary>
public sealed class OllamaDescriptor : IProviderDescriptor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;

    public OllamaDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "ollama";
    public string DisplayName => "Ollama";
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.None];
    public string DefaultEndpoint => "http://localhost:11434";
    public string ModelListingPath => "/api/tags";
    public CredentialInputMode CredentialMode => CredentialInputMode.EndpointOnly;
    public string? ApiKeyGuidanceUrl => null;

    public async Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(entry.Endpoint)
                ? DefaultEndpoint
                : entry.Endpoint.TrimEnd('/');
            var url = $"{baseUrl}{ModelListingPath}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
                return FailForStatus(response.StatusCode, baseUrl);

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
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

    private static ProviderProbeResult FailForStatus(HttpStatusCode statusCode, string endpoint)
    {
        var message = statusCode switch
        {
            HttpStatusCode.NotFound =>
                $"Endpoint not found at {endpoint}. Is the service running?",
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                $"The ollama service returned {(int)statusCode}. It may be experiencing issues.",
            _ =>
                $"ollama returned HTTP {(int)statusCode}."
        };

        return new ProviderProbeResult(false, message, []);
    }
}
