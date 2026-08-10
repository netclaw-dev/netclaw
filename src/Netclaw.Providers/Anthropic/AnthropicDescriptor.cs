// -----------------------------------------------------------------------
// <copyright file="AnthropicDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Providers.Anthropic;

/// <summary>
/// Provider descriptor for Anthropic.
/// </summary>
public sealed class AnthropicDescriptor : IProviderDescriptor
{
    private readonly HttpClient _httpClient;

    public AnthropicDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "anthropic";
    public string DisplayName => "Anthropic";
    public string DefaultEndpoint => "https://api.anthropic.com";
    public string ModelListingPath => "/v1/models";
    public IProviderAuth Auth { get; } = new ApiKeyAuth
    {
        GuidanceUrl = new Uri("https://console.anthropic.com/settings/keys"),
    };

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
            return Task.FromResult(new ProviderProbeResult(false, "API key is required for Anthropic.", []));

        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            request =>
            {
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
            },
            ProbeHelpers.ParseOpenAiStyleModels,
            ct);
    }
}
