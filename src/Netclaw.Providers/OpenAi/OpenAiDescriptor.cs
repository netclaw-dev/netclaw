// -----------------------------------------------------------------------
// <copyright file="OpenAiDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using Netclaw.Configuration;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Unified provider descriptor for OpenAI — supports both API key and OAuth (Codex) authentication.
/// </summary>
/// <remarks>
/// <para>
/// When authenticated via OAuth (ChatGPT subscription), requests route to the Codex backend
/// at <c>chatgpt.com/backend-api/codex</c>. API key authentication uses <c>api.openai.com</c>.
/// </para>
/// </remarks>
public sealed class OpenAiDescriptor : IProviderDescriptor
{
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public OpenAiDescriptor(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string TypeKey => "openai";
    public string DisplayName => "OpenAI";
    public string DefaultEndpoint => "https://api.openai.com";
    public string ModelListingPath => "/v1/models";

    private static readonly IReadOnlyDictionary<string, string> CodexExtraAuthParams =
        new Dictionary<string, string>
        {
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "netclaw",
        };

    public IProviderAuth Auth { get; } = new MultiAuth
    {
        SupportedAuthMethods = [AuthMethod.OAuthPkce, AuthMethod.ApiKey],
        GuidanceUrl = new Uri("https://platform.openai.com/api-keys"),
        OAuth = new OAuthAuth
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
        },
        AuthMethodLabels = new Dictionary<AuthMethod, string>
        {
            [AuthMethod.OAuthPkce] = "ChatGPT Subscription (recommended)",
            [AuthMethod.ApiKey] = "API Key (platform.openai.com)",
        },
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
        new() { ModelId = new("gpt-5.4"),      ContextWindowTokens = 256_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = new("gpt-5"),        ContextWindowTokens = 256_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = new("gpt-5-mini"),   ContextWindowTokens = 1_047_576, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = new("gpt-5-nano"),   ContextWindowTokens = 1_047_576, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = new("gpt-4.1"),      ContextWindowTokens = 1_047_576, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = new("gpt-4.1-mini"), ContextWindowTokens = 1_047_576, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = new("gpt-4.1-nano"), ContextWindowTokens = 1_047_576, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        // Reasoning — text+image input, text output
        new() { ModelId = new("o3"),           ContextWindowTokens = 200_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = new("o3-mini"),      ContextWindowTokens = 200_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = new("o4-mini"),      ContextWindowTokens = 200_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        // Codex (coding-optimized) — text+image input, text output
        new() { ModelId = new("gpt-5.3-codex"), ContextWindowTokens = 256_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = new("gpt-5.2-codex"), ContextWindowTokens = 256_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
        new() { ModelId = new("gpt-5-codex"),   ContextWindowTokens = 256_000, InputModalities = TextImage, OutputModalities = ModelModality.Text },
    ];

    private const ModelModality TextImage = ModelModality.Text | ModelModality.Image;

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        if (entry.AuthMethod is AuthMethod.OAuthPkce or AuthMethod.OAuthDevice)
            return ProbeOAuthAsync(entry);

        return ProbeApiKeyAsync(entry, ct);
    }

    private Task<ProviderProbeResult> ProbeOAuthAsync(ProviderEntry entry)
    {
        var token = entry.OAuthAccessToken?.Value;
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(new ProviderProbeResult(false,
                "OAuth token is required for OpenAI. Run 'netclaw provider add' with OAuth.", []));

        if (entry.OAuthTokenExpiry is { } expiry && expiry < _timeProvider.GetUtcNow())
            return Task.FromResult(new ProviderProbeResult(false,
                $"OAuth token expired {expiry:g}. Re-authenticate with 'netclaw provider fix <name>'.", []));

        // Codex tokens can't probe — return curated models
        return Task.FromResult(new ProviderProbeResult(true, null, CuratedModels));
    }

    private Task<ProviderProbeResult> ProbeApiKeyAsync(ProviderEntry entry, CancellationToken ct)
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
