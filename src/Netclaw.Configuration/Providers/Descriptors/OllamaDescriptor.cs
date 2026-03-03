using System.Net;
using System.Text.Json;

namespace Netclaw.Configuration.Providers.Descriptors;

/// <summary>
/// Provider descriptor for Ollama (local inference server).
/// </summary>
public sealed class OllamaDescriptor : IProviderDescriptor
{
    public const string DefaultEndpointValue = "http://localhost:11434";

    private readonly HttpClient _httpClient;

    public OllamaDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "ollama";
    public string DisplayName => "Ollama";
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.None];
    public string DefaultEndpoint => DefaultEndpointValue;
    public string ModelListingPath => "/api/tags";
    public CredentialInputMode CredentialMode => CredentialInputMode.EndpointOnly;
    public string? ApiKeyGuidanceUrl => null;
    public string? OAuthDeviceEndpoint => null;
    public string? OAuthTokenEndpoint => null;
    public string? OAuthDefaultClientId => null;

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            configureRequest: _ => { }, // No auth headers needed
            parseResponse: ParseOllamaModels,
            ct);
    }

    private static ProviderProbeResult ParseOllamaModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
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
}
