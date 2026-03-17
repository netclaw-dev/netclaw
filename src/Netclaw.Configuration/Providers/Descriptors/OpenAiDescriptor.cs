using System.Net.Http.Headers;

namespace Netclaw.Configuration.Providers.Descriptors;

/// <summary>
/// Provider descriptor for OpenAI with OAuth support via the Codex CLI public client.
/// </summary>
/// <remarks>
/// <para><b>OpenAI OAuth limitations (Codex CLI client ID):</b></para>
/// <list type="bullet">
///   <item><c>/v1/models</c> returns 403 "Missing scopes: api.model.read"</item>
///   <item><c>/v1/chat/completions</c> returns 429 "insufficient_quota" (use Responses API instead)</item>
///   <item>API scope names (<c>model.request</c>, <c>api.model.read</c>) are NOT valid OAuth scopes —
///         the authorization endpoint rejects them as "invalid scope"</item>
/// </list>
/// <para>
/// Only identity scopes work: <c>openid profile email offline_access</c>.
/// The Codex client implicitly grants Responses API access.
/// When an OAuth token is present, we return a curated model list instead of probing
/// <c>/v1/models</c>. API keys still probe live.
/// </para>
/// <para><b>Client ID:</b> <c>app_EMoamEEZ73f0CkXaXp7hrann</c> — the Codex CLI public client.
/// All third-party tools (OpenCode, OpenClaw, etc.) reuse this because OpenAI does not
/// provide a public API for registering custom OAuth clients.</para>
/// <para><b>Redirect URI:</b> <c>http://localhost:1455/auth/callback</c> — registered with OpenAI
/// for the Codex CLI client. Any other redirect_uri produces "unknown_error".</para>
/// <para>
/// See: https://developers.openai.com/docs/guides/migrate-to-responses,
/// https://github.com/anomalyco/opencode/issues/3281,
/// https://github.com/7shi/codex-oauth,
/// https://docs.openclaw.ai/concepts/oauth
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
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.OAuthPkce, AuthMethod.ApiKey];
    public string DefaultEndpoint => "https://api.openai.com";
    public string ModelListingPath => "/v1/models";
    public CredentialInputMode CredentialMode => CredentialInputMode.ApiKey;
    public string? ApiKeyGuidanceUrl => "https://platform.openai.com/api-keys";
    public string? OAuthDeviceEndpoint => "https://auth.openai.com/api/accounts/deviceauth/usercode";
    public string? OAuthTokenEndpoint => "https://auth.openai.com/oauth/token";
    public string? OAuthPollingEndpoint => "https://auth.openai.com/api/accounts/deviceauth/token";
    public bool UseProprietaryDeviceFlow => true;
    public string? OAuthScope => "openid profile email offline_access";
    public string? OAuthAuthorizationEndpoint => "https://auth.openai.com/oauth/authorize";
    public string? OAuthDefaultClientId => "app_EMoamEEZ73f0CkXaXp7hrann";
    public string? OAuthRedirectUri => "http://localhost:1455/auth/callback";
    public IReadOnlyDictionary<string, string>? OAuthExtraAuthParams => new Dictionary<string, string>
    {
        ["id_token_add_organizations"] = "true",
        ["codex_cli_simplified_flow"] = "true",
        ["originator"] = "netclaw",
    };

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
