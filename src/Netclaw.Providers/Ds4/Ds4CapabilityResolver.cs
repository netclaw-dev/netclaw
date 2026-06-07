// -----------------------------------------------------------------------
// <copyright file="Ds4CapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;

namespace Netclaw.Providers.Ds4;

/// <summary>
/// Resolves model capabilities for DwarfStar (ds4) by reading its
/// OpenRouter-shaped <c>/v1/models</c> listing. Extracts the context window
/// from <c>context_length</c> / <c>top_provider.context_length</c> — fields the
/// generic OpenAI-compatible resolver does not understand. ds4 currently serves
/// text-only DeepSeek V4 models, so modalities are reported as text in/text out.
/// </summary>
public sealed class Ds4CapabilityResolver : IModelCapabilityResolver
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<Ds4CapabilityResolver> _logger;
    private readonly OpenAiCompatibleEndpoint _endpoint;

    public Ds4CapabilityResolver(
        HttpClient httpClient,
        ILogger<Ds4CapabilityResolver> logger,
        string endpoint,
        string? apiKey = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(endpoint, apiKey);
        _httpClient.BaseAddress ??= _endpoint.BaseUri;
    }

    public string? ProviderType => "ds4";

    public async Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint.ModelsPath);
            if (!string.IsNullOrWhiteSpace(_endpoint.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "ds4 /v1/models returned {Status} for model {ModelId}",
                    response.StatusCode, modelId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseModels(json, modelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ds4 capability detection failed for {ModelId}", modelId);
            return null;
        }
    }

    /// <summary>
    /// Parses ds4's <c>/v1/models</c> response and returns capabilities for the
    /// matching model id. Returns null when no entry matches.
    /// </summary>
    internal static ResolvedModelCapabilities? ParseModels(string json, string modelId)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var model in data.EnumerateArray())
        {
            if (model.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedModelCapabilities(
                    modelId,
                    ModelModality.Text,
                    ModelModality.Text,
                    Ds4Descriptor.ReadContextLength(model));
            }
        }

        return null;
    }
}
