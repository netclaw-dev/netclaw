using System.Net.Http.Headers;

namespace Netclaw.Configuration.Providers.Descriptors;

/// <summary>
/// Provider descriptor for OpenAI.
/// </summary>
public sealed class OpenAiDescriptor : IProviderDescriptor
{
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
    public string? OAuthDeviceEndpoint => "https://auth.openai.com/codex/device";
    public string? OAuthTokenEndpoint => "https://auth.openai.com/oauth/token";
    public string? OAuthDefaultClientId => "app_EMoamEEZ73f0CkXaXp7hrann";

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var bearerToken = entry.ApiKey?.Value ?? entry.OAuthAccessToken?.Value;
        if (string.IsNullOrWhiteSpace(bearerToken))
            return Task.FromResult(new ProviderProbeResult(false, "API key or OAuth token is required for OpenAI.", []));

        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken),
            ProbeHelpers.ParseOpenAiStyleModels,
            ct);
    }
}
