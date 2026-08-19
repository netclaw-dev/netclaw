// -----------------------------------------------------------------------
// <copyright file="ZaiDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using Netclaw.Configuration;

namespace Netclaw.Providers.Zai;

/// <summary>
/// Provider descriptor for Z.ai's hosted OpenAI-compatible API (GLM models).
/// Default endpoint targets the GLM Coding Plan; pay-as-you-go operators
/// override Endpoint with https://api.z.ai/api/paas/v4.
/// </summary>
public sealed class ZaiDescriptor(HttpClient httpClient) : IProviderDescriptor
{
    private const int FlagshipContextWindow = 1_000_000;
    private const int PreviousContextWindow = 200_000;

    public string TypeKey => "zai";

    public string DisplayName => "Z.ai";

    public string DefaultEndpoint => "https://api.z.ai/api/coding/paas/v4";

    public string ModelListingPath => "/models";

    public IProviderAuth Auth { get; } = new ApiKeyAuth
    {
        GuidanceUrl = new Uri("https://z.ai/manage-apikey/apikey-list"),
    };

    public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult(new ProviderProbeResult(
                false,
                "API key is required for Z.ai. Get one at https://z.ai/manage-apikey/apikey-list",
                []));
        }

        return ProbeHelpers.ExecuteProbeAsync(
            httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey),
            ParseModels,
            ct);
    }

    internal static ProviderProbeResult ParseModels(string json)
    {
        var parsed = ProbeHelpers.ParseOpenAiStyleModels(json);
        var models = parsed.Models
            .Select(model => KnownModelContextWindows.TryGetValue(model.ModelId.Value, out var contextWindow)
                ? model with
                {
                    ContextWindowTokens = contextWindow,
                    InputModalities = ModelModality.Text,
                    OutputModalities = ModelModality.Text,
                }
                : model)
            .ToArray();

        return parsed with { Models = models };
    }

    private static readonly Dictionary<string, int> KnownModelContextWindows =
        new(StringComparer.Ordinal)
        {
            ["glm-5.3"] = FlagshipContextWindow,
            ["glm-5.2"] = PreviousContextWindow,
        };
}
