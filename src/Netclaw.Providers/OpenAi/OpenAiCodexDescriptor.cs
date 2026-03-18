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
    public string DefaultEndpoint => "https://chatgpt.com/backend-api/codex";
    public string ModelListingPath => string.Empty; // no model listing endpoint

    private static readonly IReadOnlyDictionary<string, string> CodexExtraAuthParams =
        new Dictionary<string, string>
        {
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "netclaw",
        };

    public IProviderAuth Auth { get; } = new OAuthAuth
    {
        SupportedAuthMethods = [AuthMethod.OAuthPkce],
        TokenEndpoint = new Uri("https://auth.openai.com/oauth/token"),
        ClientId = "app_EMoamEEZ73f0CkXaXp7hrann",
        DeviceEndpoint = new Uri("https://auth.openai.com/api/accounts/deviceauth/usercode"),
        AuthorizationEndpoint = new Uri("https://auth.openai.com/oauth/authorize"),
        RedirectUri = new Uri("http://localhost:1455/auth/callback"),
        Scope = "openid profile email offline_access",
        PollingEndpoint = new Uri("https://auth.openai.com/api/accounts/deviceauth/token"),
        ExtraAuthParams = CodexExtraAuthParams,
        UseProprietaryDeviceFlow = true,
    };

    /// <summary>
    /// Curated model list — Codex tokens cannot call /v1/models.
    /// Includes context window sizes and modality metadata so the runtime
    /// doesn't fall back to the 32K default.
    /// </summary>
    // Last updated: 2026-03-18 from https://developers.openai.com/api/docs/models/all
    // Run /update-openai-models skill to refresh this list when new models ship.
    internal static readonly DiscoveredModel[] CuratedModels =
    [
        // Frontier — all accept text+image input, produce text output
        new() { ModelId = "gpt-5.4",      ContextWindowTokens = 256_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = "gpt-5",        ContextWindowTokens = 256_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = "gpt-5-mini",   ContextWindowTokens = 1_047_576, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = "gpt-5-nano",   ContextWindowTokens = 1_047_576, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = "gpt-4.1",      ContextWindowTokens = 1_047_576, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = "gpt-4.1-mini", ContextWindowTokens = 1_047_576, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = "gpt-4.1-nano", ContextWindowTokens = 1_047_576, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        // Reasoning — text+image input, text output
        new() { ModelId = "o3",           ContextWindowTokens = 200_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = "o3-mini",      ContextWindowTokens = 200_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = "o4-mini",      ContextWindowTokens = 200_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        // Codex (coding-optimized) — text+image input, text output
        new() { ModelId = "gpt-5.3-codex", ContextWindowTokens = 256_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = "gpt-5.2-codex", ContextWindowTokens = 256_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = "gpt-5-codex",   ContextWindowTokens = 256_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
    ];

    private const ModelModality TextImage = ModelModality.Text | ModelModality.Image;

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
