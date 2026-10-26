// -----------------------------------------------------------------------
// <copyright file="AnthropicCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Providers.Anthropic;

/// <summary>
/// Reads the thinking capability and the maximum output of a model from the Anthropic Models API
/// (<c>GET /v1/models/{id}</c>).
/// </summary>
public sealed class AnthropicCapabilityResolver : IModelCapabilityResolver
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AnthropicCapabilityResolver> _logger;
    private readonly string _endpoint;
    private readonly string _apiKey;

    public AnthropicCapabilityResolver(
        HttpClient httpClient, ILogger<AnthropicCapabilityResolver> logger, string endpoint, string apiKey)
    {
        _httpClient = httpClient;
        _logger = logger;
        _endpoint = endpoint.TrimEnd('/');
        _apiKey = apiKey;
    }

    public string? ProviderType => "anthropic";

    public async Task<ResolvedModelCapabilities?> ResolveAsync(string modelId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{_endpoint}/v1/models/{Uri.EscapeDataString(modelId)}");
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Anthropic Models API returned {Status} for model {ModelId}", response.StatusCode, modelId);
            return null;
        }

        return ParseModel(await response.Content.ReadAsStringAsync(ct), modelId);
    }

    /// <summary>
    /// Parses one Models API model object. The API does not report whether a model rejects
    /// <c>thinking.type: disabled</c> (design Open Question 3), so a thinking model offers Off, and
    /// the provider error is loud when a model rejects it.
    /// </summary>
    internal static ResolvedModelCapabilities? ParseModel(string json, string modelId)
    {
        using var document = JsonDocument.Parse(json);
        return ParseModel(document.RootElement, modelId);
    }

    internal static ResolvedModelCapabilities? ParseModel(JsonElement root, string modelId)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        return new ResolvedModelCapabilities(modelId, null, null)
        {
            Thinking = ParseThinking(root),
            MaxOutputTokens = ProbeHelpers.TryReadPositiveInt32(root, "max_tokens"),
        };
    }

    private static ThinkingSupport ParseThinking(JsonElement root)
    {
        if (!root.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object
            || !capabilities.TryGetProperty("thinking", out var thinking) || thinking.ValueKind != JsonValueKind.Object)
            return ThinkingSupport.Unknown;
        if (!IsSupported(thinking))
            return ThinkingSupport.None;

        // Netclaw sends a level in adaptive mode, so a model without adaptive thinking gets only Off.
        var levels = new List<ThinkingLevel> { ThinkingLevel.Off };
        var adaptive = thinking.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Object
                       && types.TryGetProperty("adaptive", out var adaptiveType) && IsSupported(adaptiveType);
        if (adaptive && capabilities.TryGetProperty("effort", out var effort) && IsSupported(effort))
        {
            // "xhigh" has no Netclaw level: Netclaw Max sends "max" to Anthropic.
            foreach (var (name, level) in new[]
                     {
                         ("low", ThinkingLevel.Low), ("medium", ThinkingLevel.Medium),
                         ("high", ThinkingLevel.High), ("max", ThinkingLevel.Max),
                     })
            {
                if (effort.TryGetProperty(name, out var flag) && IsSupported(flag))
                    levels.Add(level);
            }
        }

        return ThinkingSupport.Controllable([.. levels]);
    }

    private static bool IsSupported(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty("supported", out var supported)
           && supported.ValueKind == JsonValueKind.True;
}
