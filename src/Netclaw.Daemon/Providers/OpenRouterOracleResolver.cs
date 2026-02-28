using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Resolves model capabilities by querying OpenRouter's public
/// <c>GET /api/v1/models</c> endpoint. Caches the full model list
/// on first call. No API key required for reads.
/// </summary>
public sealed class OpenRouterOracleResolver : IModelCapabilityResolver
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenRouterOracleResolver> _logger;
    private Dictionary<string, ResolvedModelCapabilities>? _cache;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public OpenRouterOracleResolver(HttpClient httpClient, ILogger<OpenRouterOracleResolver> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId, CancellationToken ct = default)
    {
        var catalog = await GetOrFetchCatalogAsync(ct);
        if (catalog is null)
            return null;

        // Try all normalized candidates
        foreach (var candidate in ModelIdNormalizer.GetCandidates(modelId))
        {
            if (catalog.TryGetValue(candidate, out var caps))
                return caps with { ModelId = modelId };
        }

        _logger.LogDebug("Model {ModelId} not found in OpenRouter catalog", modelId);
        return null;
    }

    private async Task<Dictionary<string, ResolvedModelCapabilities>?> GetOrFetchCatalogAsync(
        CancellationToken ct)
    {
        if (_cache is not null)
            return _cache;

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_cache is not null)
                return _cache;

            _cache = await FetchCatalogAsync(ct);
            return _cache;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<Dictionary<string, ResolvedModelCapabilities>?> FetchCatalogAsync(
        CancellationToken ct)
    {
        var url = $"{ProviderCapabilities.GetDefaultEndpoint("openrouter")}" +
                  $"{ProviderCapabilities.GetModelListingPath("openrouter")}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter oracle returned {Status}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseCatalog(json);
    }

    internal static Dictionary<string, ResolvedModelCapabilities> ParseCatalog(string json)
    {
        var result = new Dictionary<string, ResolvedModelCapabilities>(StringComparer.OrdinalIgnoreCase);
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var dataArray))
            return result;

        foreach (var model in dataArray.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out var idProp))
                continue;

            var id = idProp.GetString();
            if (id is null) continue;

            var input = ModelModality.Text;
            var output = ModelModality.Text;

            if (model.TryGetProperty("architecture", out var arch))
            {
                if (arch.TryGetProperty("input_modalities", out var inputMods))
                    input = ParseModalityArray(inputMods);
                if (arch.TryGetProperty("output_modalities", out var outputMods))
                    output = ParseModalityArray(outputMods);
            }

            result[id] = new ResolvedModelCapabilities(id, input, output);
        }

        return result;
    }

    internal static ModelModality ParseModalityArray(JsonElement array)
    {
        var result = ModelModality.None;

        foreach (var item in array.EnumerateArray())
        {
            var value = item.GetString();
            if (value is null) continue;

            result |= value.ToLowerInvariant() switch
            {
                "text" => ModelModality.Text,
                "image" => ModelModality.Image,
                "audio" => ModelModality.Audio,
                "video" => ModelModality.Video,
                _ => ModelModality.None,
            };
        }

        return result;
    }
}
