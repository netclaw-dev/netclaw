// -----------------------------------------------------------------------
// <copyright file="OllamaCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Resolves model capabilities by querying the Ollama <c>POST /api/show</c>
/// endpoint. Extracts context window and vision support from the
/// <c>model_info</c> object, which uses architecture-prefixed flat keys
/// (e.g. <c>"qwen35.context_length"</c>).
/// </summary>
public sealed class OllamaCapabilityResolver : IModelCapabilityResolver
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaCapabilityResolver> _logger;
    private readonly string _endpoint;

    public OllamaCapabilityResolver(
        HttpClient httpClient,
        ILogger<OllamaCapabilityResolver> logger,
        string endpoint)
    {
        _httpClient = httpClient;
        _logger = logger;
        _endpoint = endpoint.TrimEnd('/');
    }

    public string? ProviderType => "ollama";

    public async Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId, CancellationToken ct = default)
    {
        try
        {
            var requestBody = JsonSerializer.Serialize(new { name = modelId });
            using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{_endpoint}/api/show", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Ollama /api/show returned {Status} for model {ModelId}",
                    response.StatusCode, modelId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseShowResponse(json, modelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "Ollama capability detection failed for {ModelId}", modelId);
            return null;
        }
    }

    /// <summary>
    /// Parses the Ollama <c>/api/show</c> JSON response to extract model
    /// capabilities. Architecture-prefixed keys in <c>model_info</c> are
    /// used to determine context window and vision support.
    /// </summary>
    internal static ResolvedModelCapabilities? ParseShowResponse(string json, string modelId)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("model_info", out var modelInfo))
            return null;

        // Read the architecture prefix (e.g. "qwen35", "llava")
        string? arch = null;
        if (modelInfo.TryGetProperty("general.architecture", out var archProp))
            arch = archProp.GetString();

        int? contextWindow = null;
        var inputModalities = ModelModality.Text;

        if (arch is not null)
        {
            // Context window: "{arch}.context_length"
            contextWindow = ProbeHelpers.TryReadPositiveInt32(modelInfo, $"{arch}.context_length");

            // Vision: "{arch}.vision.block_count" > 0 means image input
            if (modelInfo.TryGetProperty($"{arch}.vision.block_count", out var visionProp) &&
                visionProp.ValueKind == JsonValueKind.Number &&
                visionProp.GetInt32() > 0)
            {
                inputModalities = ModelModality.Text | ModelModality.Image;
            }
        }

        // Ollama only serves text generators — output is always text
        return new ResolvedModelCapabilities(modelId, inputModalities, ModelModality.Text, contextWindow);
    }
}
