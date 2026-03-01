using System.Net.Http.Headers;

namespace Netclaw.Configuration.Providers.Descriptors;

/// <summary>
/// Provider descriptor for OpenRouter.
/// </summary>
public sealed class OpenRouterDescriptor : IProviderDescriptor
{
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

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
            return Task.FromResult(new ProviderProbeResult(false, "API key is required for OpenRouter.", []));

        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey),
            ProbeHelpers.ParseOpenAiStyleModels,
            ct);
    }
}
