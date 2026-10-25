// -----------------------------------------------------------------------
// <copyright file="VeniceAiDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using Netclaw.Configuration;

namespace Netclaw.Providers.VeniceAi;

/// <summary>
/// Provider descriptor for Venice.ai.
/// </summary>
public sealed class VeniceAiDescriptor : IProviderDescriptor
{
    private readonly HttpClient _httpClient;

    public VeniceAiDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "veniceai";

    public ThinkingDialect ResolveThinkingDialect(ProviderEntry entry) => ThinkingDialect.Native;
    public string DisplayName => "Venice.ai";

    // Venice's OpenAI-compatible API lives under /api/v1 (not /v1).
    // Pair with ModelListingPath = "/models" so probes target /api/v1/models.
    public string DefaultEndpoint => "https://api.venice.ai/api/v1";
    public string ModelListingPath => "/models";

    public IProviderAuth Auth { get; } = new ApiKeyAuth
    {
        GuidanceUrl = new Uri("https://venice.ai/settings/api"),
    };

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
            return Task.FromResult(new ProviderProbeResult(false,
                "API key is required for Venice. Get one at https://venice.ai/settings/api", []));

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
