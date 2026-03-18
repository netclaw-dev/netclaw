using System.Net.Http.Headers;
using Netclaw.Configuration;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Provider descriptor for OpenAI with API key authentication.
/// </summary>
/// <remarks>
/// <para>
/// For OAuth/Codex tokens, use <see cref="OpenAiCodexDescriptor"/> (type key: "openai-codex").
/// Codex OAuth tokens CANNOT call <c>api.openai.com</c> — they require the Codex backend
/// at <c>chatgpt.com/backend-api/codex</c>.
/// </para>
/// </remarks>
public sealed class OpenAiDescriptor : IProviderDescriptor
{
    private readonly HttpClient _httpClient;

    public OpenAiDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "openai";
    public string DisplayName => "OpenAI";
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.ApiKey];
    public string DefaultEndpoint => "https://api.openai.com";
    public string ModelListingPath => "/v1/models";
    public CredentialInputMode CredentialMode => CredentialInputMode.ApiKey;
    public string? ApiKeyGuidanceUrl => "https://platform.openai.com/api-keys";

    // No OAuth — see OpenAiCodexDescriptor
    public string? OAuthDeviceEndpoint => null;
    public string? OAuthTokenEndpoint => null;
    public string? OAuthDefaultClientId => null;

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
            return Task.FromResult(new ProviderProbeResult(false,
                "API key is required for OpenAI. Get one at https://platform.openai.com/api-keys", []));

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
