using System.Net.Http.Headers;

namespace Netclaw.Configuration.Providers.Descriptors;

/// <summary>
/// Provider descriptor for OpenAI-compatible endpoints such as vLLM or Lemonade.
/// </summary>
public sealed class OpenAiCompatibleDescriptor : IProviderDescriptor
{
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "openai-compatible";
    public string DisplayName => "OpenAI-Compatible";
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.None];
    public string DefaultEndpoint => "http://localhost:11434";
    public string ModelListingPath => "/v1/models";
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
            request =>
            {
                var apiKey = entry.ApiKey?.Value;
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            },
            ProbeHelpers.ParseOpenAiStyleModels,
            ct);
    }
}
