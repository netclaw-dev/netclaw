using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Resolves model capabilities from the HuggingFace Hub API by mapping
/// the <c>pipeline_tag</c> field to <see cref="ModelModality"/> flags.
/// Fallback source for open-source models not in the OpenRouter catalog.
/// </summary>
public sealed class HuggingFaceCapabilityResolver : IModelCapabilityResolver
{
    private const string BaseUrl = "https://huggingface.co/api/models/";
    private const int MaxHttpAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly ILogger<HuggingFaceCapabilityResolver> _logger;

    public HuggingFaceCapabilityResolver(HttpClient httpClient, ILogger<HuggingFaceCapabilityResolver> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId, CancellationToken ct = default)
    {
        // Use normalizer candidates, but only try those with org/model format (HF requires a slash)
        var candidates = ModelIdNormalizer.GetCandidates(modelId)
            .Where(c => c.Contains('/', StringComparison.Ordinal))
            .Take(MaxHttpAttempts);

        foreach (var candidate in candidates)
        {
            var result = await TryResolveHfIdAsync(candidate, modelId, ct);
            if (result is not null)
                return result;
        }

        return null;
    }

    private async Task<ResolvedModelCapabilities?> TryResolveHfIdAsync(
        string hfId, string originalModelId, CancellationToken ct)
    {
        var url = $"{BaseUrl}{Uri.EscapeDataString(hfId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("HuggingFace returned {Status} for {HfId}", response.StatusCode, hfId);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseModelInfo(originalModelId, json);
    }

    internal static ResolvedModelCapabilities? ParseModelInfo(string modelId, string json)
    {
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("pipeline_tag", out var tagProp))
            return null;

        var tag = tagProp.GetString();
        if (tag is null) return null;

        var (input, output) = MapPipelineTag(tag);
        return new ResolvedModelCapabilities(modelId, input, output);
    }

    internal static (ModelModality Input, ModelModality Output) MapPipelineTag(string pipelineTag)
    {
        return pipelineTag.ToLowerInvariant() switch
        {
            "text-generation" => (ModelModality.Text, ModelModality.Text),
            "text2text-generation" => (ModelModality.Text, ModelModality.Text),
            "image-text-to-text" => (ModelModality.Text | ModelModality.Image, ModelModality.Text),
            "visual-question-answering" => (ModelModality.Text | ModelModality.Image, ModelModality.Text),
            "image-to-text" => (ModelModality.Image, ModelModality.Text),
            "text-to-image" => (ModelModality.Text, ModelModality.Image),
            "text-to-audio" => (ModelModality.Text, ModelModality.Audio),
            "text-to-speech" => (ModelModality.Text, ModelModality.Audio),
            "automatic-speech-recognition" => (ModelModality.Audio, ModelModality.Text),
            "audio-to-audio" => (ModelModality.Audio, ModelModality.Audio),
            "text-to-video" => (ModelModality.Text, ModelModality.Video),
            "video-text-to-text" => (ModelModality.Text | ModelModality.Video, ModelModality.Text),
            _ => (ModelModality.Text, ModelModality.Text),
        };
    }
}
