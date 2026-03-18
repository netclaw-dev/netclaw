using Netclaw.Configuration;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Provider descriptor for OpenAI Codex (OAuth tokens only).
/// </summary>
/// <remarks>
/// <para>
/// Codex OAuth tokens are issued by the Codex CLI public client
/// (<c>app_EMoamEEZ73f0CkXaXp7hrann</c>) and CANNOT call <c>api.openai.com</c> at all.
/// Every endpoint there returns 401, 403, or 429.
/// </para>
/// <para><b>Codex backend:</b> <c>https://chatgpt.com/backend-api/codex</c>.
/// This is a completely separate API surface that wraps the Responses API behind
/// the ChatGPT backend. It requires:</para>
/// <list type="bullet">
///   <item><c>ChatGPT-Account-Id</c> header extracted from the JWT <c>oid</c> claim</item>
///   <item><c>"store": false</c> in the request body</item>
///   <item>Bearer token from the Codex OAuth flow</item>
/// </list>
/// <para>
/// See: https://github.com/anomalyco/opencode (plugin/codex.ts),
/// https://github.com/7shi/codex-oauth,
/// https://docs.openclaw.ai/concepts/oauth
/// </para>
/// </remarks>
public sealed class OpenAiCodexDescriptor : IProviderDescriptor
{
    private readonly TimeProvider _timeProvider;

    public OpenAiCodexDescriptor(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string TypeKey => "openai-codex";
    public string DisplayName => "OpenAI (Codex OAuth)";
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => [AuthMethod.OAuthPkce];
    public string DefaultEndpoint => "https://chatgpt.com/backend-api/codex";
    public string ModelListingPath => string.Empty; // no model listing endpoint
    public CredentialInputMode CredentialMode => CredentialInputMode.OAuthOnly;
    public string? ApiKeyGuidanceUrl => null; // no API key — OAuth only

    // OAuth configuration (same Codex CLI client)
    public string? OAuthDeviceEndpoint => "https://auth.openai.com/api/accounts/deviceauth/usercode";
    public string? OAuthTokenEndpoint => "https://auth.openai.com/oauth/token";
    public string? OAuthPollingEndpoint => "https://auth.openai.com/api/accounts/deviceauth/token";
    public bool UseProprietaryDeviceFlow => true;
    public string? OAuthScope => "openid profile email offline_access";
    public string? OAuthAuthorizationEndpoint => "https://auth.openai.com/oauth/authorize";
    public string? OAuthDefaultClientId => "app_EMoamEEZ73f0CkXaXp7hrann";
    public string? OAuthRedirectUri => "http://localhost:1455/auth/callback";
    private static readonly IReadOnlyDictionary<string, string> ExtraAuthParams =
        new Dictionary<string, string>
        {
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "netclaw",
        };

    public IReadOnlyDictionary<string, string>? OAuthExtraAuthParams => ExtraAuthParams;

    /// <summary>
    /// Curated model list — Codex tokens cannot call /v1/models.
    /// </summary>
    // Last updated: 2026-03-17 from https://developers.openai.com/api/docs/models/all
    // Run /update-openai-models skill to refresh this list when new models ship.
    internal static readonly DiscoveredModel[] CuratedModels =
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
        var token = entry.OAuthAccessToken?.Value;
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(new ProviderProbeResult(false,
                "OAuth token is required for OpenAI Codex. Run 'netclaw provider add' with OAuth.", []));

        if (entry.OAuthTokenExpiry is { } expiry && expiry < _timeProvider.GetUtcNow())
            return Task.FromResult(new ProviderProbeResult(false,
                $"OAuth token expired {expiry:g}. Re-authenticate with 'netclaw provider fix <name>'.", []));

        // Codex tokens can't probe — return curated models
        return Task.FromResult(new ProviderProbeResult(true, null, CuratedModels));
    }
}
