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
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.OAuthPkce, AuthMethod.ApiKey];
    public string DefaultEndpoint => "https://api.openai.com";
    public string ModelListingPath => "/v1/models";
    public CredentialInputMode CredentialMode => CredentialInputMode.ApiKey;
    public string? ApiKeyGuidanceUrl => "https://platform.openai.com/api-keys";
    public string? OAuthDeviceEndpoint => "https://auth.openai.com/api/accounts/deviceauth/usercode";
    public string? OAuthTokenEndpoint => "https://auth.openai.com/oauth/token";
    public string? OAuthPollingEndpoint => "https://auth.openai.com/api/accounts/deviceauth/token";
    public bool UseProprietaryDeviceFlow => true;
    // Identity scopes only. API scopes (model.request, api.model.read) are NOT
    // valid OAuth scope names — they appear in API error messages but are rejected
    // by the authorization endpoint as "invalid scope". OpenCode uses identity-only
    // scopes successfully. The Codex client may grant API access implicitly.
    public string? OAuthScope => "openid profile email offline_access";
    public string? OAuthAuthorizationEndpoint => "https://auth.openai.com/oauth/authorize";

    // The Codex CLI public client ID. All third-party tools (OpenCode, OpenClaw, etc.) reuse
    // this because OpenAI does not provide a public API for registering custom OAuth clients.
    // The redirect_uri and extra auth params below must match what's registered for this client.
    // Sources:
    //   - OpenCode: https://github.com/anomalyco/opencode/issues/3281
    //   - codex-oauth: https://github.com/7shi/codex-oauth
    //   - OpenClaw: https://docs.openclaw.ai/concepts/oauth
    public string? OAuthDefaultClientId => "app_EMoamEEZ73f0CkXaXp7hrann";

    // Port 1455 and path /auth/callback are registered with OpenAI for the Codex CLI client ID.
    // Using any other redirect_uri produces "unknown_error" from auth.openai.com.
    public string? OAuthRedirectUri => "http://localhost:1455/auth/callback";

    // OpenAI-specific authorization parameters required by the Codex OAuth flow.
    // Without these, the authorization endpoint returns errors.
    public IReadOnlyDictionary<string, string>? OAuthExtraAuthParams => new Dictionary<string, string>
    {
        ["id_token_add_organizations"] = "true",
        ["codex_cli_simplified_flow"] = "true",
        ["originator"] = "netclaw",
    };

    // OpenAI-specific limitation: OAuth tokens obtained via the Codex CLI client ID
    // CANNOT call /v1/models (HTTP 403 "Missing scopes: api.model.read"). The scope
    // names in the error (model.request, api.model.read) are NOT valid OAuth scope
    // values — the authorization endpoint rejects them as "invalid scope".
    // This is an OpenAI-specific issue; other OAuth providers may support live model
    // listing. OpenCode and OpenClaw both use curated/hardcoded model lists instead.
    // When an OAuth token is present, we return a curated model list instead of probing.
    // API keys still probe /v1/models normally.
    //
    // See: https://github.com/openclaw/openclaw/issues/24720
    //      https://developers.openai.com/codex/models

    /// <summary>
    /// Curated model list for OAuth tokens that cannot call /v1/models.
    /// </summary>
    // Last updated: 2026-03-17 from https://developers.openai.com/api/docs/models/all
    // Run /update-openai-models skill to refresh this list when new models ship.
    private static readonly DiscoveredModel[] CuratedModels =
    [
        // Frontier
        new() { ModelId = "gpt-5.4" },
        new() { ModelId = "gpt-5" },
        new() { ModelId = "gpt-5-mini" },
        new() { ModelId = "gpt-5-nano" },
        new() { ModelId = "gpt-4.1" },
        new() { ModelId = "gpt-4.1-mini" },
        new() { ModelId = "gpt-4.1-nano" },
        // Reasoning
        new() { ModelId = "o3" },
        new() { ModelId = "o3-mini" },
        new() { ModelId = "o4-mini" },
        // Codex (coding-optimized)
        new() { ModelId = "gpt-5.3-codex" },
        new() { ModelId = "gpt-5.2-codex" },
        new() { ModelId = "gpt-5-codex" },
    ];

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var bearerToken = entry.ApiKey?.Value ?? entry.OAuthAccessToken?.Value;
        if (string.IsNullOrWhiteSpace(bearerToken))
            return Task.FromResult(new ProviderProbeResult(false, "API key or OAuth token is required for OpenAI.", []));

        // OAuth tokens can't call /v1/models — return curated list instead
        if (entry.OAuthAccessToken is not null)
            return Task.FromResult(new ProviderProbeResult(true, null, CuratedModels));

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
