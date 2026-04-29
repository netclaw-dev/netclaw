// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Providers.SelfHosted;

public sealed class OpenAiCompatibleCapabilityResolver : IModelCapabilityResolver
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiCompatibleCapabilityResolver> _logger;
    private readonly OpenAiCompatibleEndpoint _endpoint;

    public OpenAiCompatibleCapabilityResolver(
        HttpClient httpClient,
        ILogger<OpenAiCompatibleCapabilityResolver> logger,
        string endpoint,
        string? apiKey = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(endpoint, apiKey);
        _httpClient.BaseAddress ??= _endpoint.BaseUri;
    }

    public async Task<ResolvedModelCapabilities?> ResolveAsync(string modelId, CancellationToken ct = default)
    {
        try
        {
            using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, _endpoint.ModelsPath);
            ApplyAuth(modelsRequest);

            using var modelsResponse = await _httpClient.SendAsync(modelsRequest, ct);
            modelsResponse.EnsureSuccessStatusCode();

            var modelsJson = await modelsResponse.Content.ReadAsStringAsync(ct);
            var fromModels = ParseModelsResponse(modelsJson, modelId);

            var inputModalities = fromModels?.InputModalities ?? ModelModality.Text;
            var outputModalities = fromModels?.OutputModalities ?? ModelModality.Text;
            var contextWindow = fromModels?.ContextWindowTokens;

            using var propsRequest = new HttpRequestMessage(HttpMethod.Get, "/props");
            ApplyAuth(propsRequest);

            using var propsResponse = await _httpClient.SendAsync(propsRequest, ct);
            if (propsResponse.IsSuccessStatusCode)
            {
                var propsJson = await propsResponse.Content.ReadAsStringAsync(ct);
                var fromProps = ParsePropsResponse(propsJson, modelId, contextWindow);
                if (fromProps is not null)
                    return fromProps with { InputModalities = inputModalities | fromProps.InputModalities };
            }

            return fromModels;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "OpenAI-compatible capability detection failed for {ModelId}", modelId);
            return null;
        }
    }

    internal static ResolvedModelCapabilities? ParseModelsResponse(string json, string modelId)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
                continue;

            int? contextWindow = null;
            if (model.TryGetProperty("meta", out var meta)
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("n_ctx_train", out var ctx)
                && ctx.ValueKind == JsonValueKind.Number)
            {
                contextWindow = ctx.GetInt32();
            }

            return new ResolvedModelCapabilities(modelId, ModelModality.Text, ModelModality.Text, contextWindow);
        }

        return null;
    }

    internal static ResolvedModelCapabilities? ParsePropsResponse(string json, string modelId, int? contextWindow)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        int? effectiveContextWindow = contextWindow;
        if (root.TryGetProperty("default_generation_settings", out var defaultGenerationSettings)
            && defaultGenerationSettings.ValueKind == JsonValueKind.Object
            && defaultGenerationSettings.TryGetProperty("params", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("n_ctx", out var nCtx)
            && nCtx.ValueKind == JsonValueKind.Number)
        {
            effectiveContextWindow = nCtx.GetInt32();
        }

        var inputModalities = ModelModality.Text;
        if (root.TryGetProperty("modalities", out var modalities)
            && modalities.ValueKind == JsonValueKind.Object
            && modalities.TryGetProperty("vision", out var vision)
            && vision.ValueKind is JsonValueKind.True or JsonValueKind.False
            && vision.GetBoolean())
        {
            inputModalities |= ModelModality.Image;
        }

        return new ResolvedModelCapabilities(modelId, inputModalities, ModelModality.Text, effectiveContextWindow);
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_endpoint.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);
    }
}
