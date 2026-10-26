// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Provider descriptor for OpenAI-compatible endpoints such as llama.cpp,
/// llama-server, vLLM, Lemonade, or DwarfStar (ds4).
/// </summary>
public sealed class OpenAiCompatibleDescriptor : IProviderDescriptor
{
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "openai-compatible";

    /// <summary>
    /// The operator sets the dialect once per provider in <c>VendorOptions:ThinkingDialect</c>, or
    /// <c>netclaw model set</c> saves the dialect that it detected. Without a value the dialect is
    /// <see cref="ThinkingDialect.Unresolved"/>: no level is offered. An invalid value fails loudly.
    /// </summary>
    public ThinkingDialect ResolveThinkingDialect(ProviderEntry entry)
    {
        var raw = entry.VendorOptions?.FirstOrDefault(pair =>
            string.Equals(pair.Key, ThinkingDialectMap.VendorOptionKey, StringComparison.OrdinalIgnoreCase)).Value;
        if (raw is null)
            return ThinkingDialect.Unresolved;

        var text = raw.GetValueKind() == JsonValueKind.String ? raw.GetValue<string>() : raw.ToJsonString();
        return ThinkingDialectMap.TryParseConfigured(text, out var dialect)
            ? dialect
            : throw new InvalidOperationException(
                $"VendorOptions:{ThinkingDialectMap.VendorOptionKey} value '{text}' is not valid. "
                + $"Set it to {ThinkingDialectMap.ConfigurableDialectList}.");
    }

    public string DisplayName => "OpenAI-compatible (llama.cpp / vLLM / DwarfStar ds4)";
    public bool IsSelfHosted => true;
    public string DefaultEndpoint => "http://localhost:11434";
    public string ModelListingPath => "/v1/models";
    // Endpoint-only by default, but an operator may supply a Bearer key for
    // gateways that sit in front of a protected vLLM / llama.cpp / SGLang
    // endpoint. The clients send the key when the operator selects API-key auth.
    // This declaration lets each setup surface collect the optional key.
    public IProviderAuth Auth { get; } = new OptionalApiKeyAuth();

    public async Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var servedByLlamaCpp = false;
        var result = await ProbeModelsAsync(entry, json =>
        {
            servedByLlamaCpp = ListsLlamaCppModels(json);
            return ParseModels(json);
        }, ct);
        if (!result.Success || !servedByLlamaCpp)
            return result;

        // llama.cpp serves /props. It tells whether the chat template reads the thinking switch, so
        // the probe detects the dialect and the capability of the served model here. Another server
        // gets no detection, and the operator sets VendorOptions:ThinkingDialect.
        var propsJson = await TryReadPropsAsync(entry, ct);
        if (propsJson is null)
            return result;

        using var props = JsonDocument.Parse(propsJson);
        if (props.RootElement.ValueKind != JsonValueKind.Object)
            return result;

        var thinking = LlamaCppBackendStrategy.IsRouter(props.RootElement)
            ? ThinkingSupport.Unknown
            : LlamaCppBackendStrategy.ReadThinking(props.RootElement);
        return result with
        {
            Models = [.. result.Models.Select(model => model with { Thinking = thinking })],
            DetectedThinkingDialect = ThinkingDialect.ReasoningEffort,
        };
    }

    private async Task<string?> TryReadPropsAsync(ProviderEntry entry, CancellationToken ct)
    {
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            string.IsNullOrWhiteSpace(entry.Endpoint) ? DefaultEndpoint : entry.Endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeouts.SelfHosted);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(endpoint.BaseUri.GetLeftPart(UriPartial.Authority) + "/props"));
        OpenAiCompatibleHttp.ApplyBearerAuth(request, entry.AuthMethod is AuthMethod.ApiKey ? entry.ApiKey?.Value : null);
        try
        {
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(timeout.Token) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // The models listing already succeeded. Without /props nothing is detected, the
            // dialect stays unset, and `model set --thinking` names the key to set.
            return null;
        }
    }

    // llama.cpp marks its /v1/models entries with owned_by "llamacpp" and a meta object.
    private static bool ListsLlamaCppModels(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
               && document.RootElement.TryGetProperty("data", out var data)
               && data.ValueKind == JsonValueKind.Array
               && data.EnumerateArray().Any(model => model.ValueKind == JsonValueKind.Object
                   && ((model.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                        && owner.GetString() == "llamacpp")
                       || (model.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)));
    }

    private Task<ProviderProbeResult> ProbeModelsAsync(
        ProviderEntry entry, Func<string, ProviderProbeResult> parse, CancellationToken ct)
    {
        // Same endpoint normalisation as chat: the configured endpoint may or may not
        // end in /v1, so the models path comes from OpenAiCompatibleEndpoint rather
        // than being appended to the raw string.
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            string.IsNullOrWhiteSpace(entry.Endpoint) ? DefaultEndpoint : entry.Endpoint);

        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            endpoint.ModelsPath,
            endpoint.BaseUri.GetLeftPart(UriPartial.Authority),
            request =>
            {
                OpenAiCompatibleHttp.ApplyBearerAuth(
                    request,
                    entry.AuthMethod is AuthMethod.ApiKey ? entry.ApiKey?.Value : null);
            },
            parse,
            ct,
            timeout: ProbeTimeouts.SelfHosted);
    }

    internal static ProviderProbeResult ParseModels(string json)
        => ProbeHelpers.ParseOpenAiStyleModels(json, TryReadContextWindow);

    private static int? TryReadContextWindow(JsonElement model)
    {
        var contextWindow = ProbeHelpers.TryReadPositiveInt32(model, "max_model_len"); // vLLM
        if (contextWindow is not null)
            return contextWindow;

        // ds4 (and other OpenRouter-shaped backends): context_length /
        // top_provider.context_length.
        contextWindow = Ds4BackendStrategy.ReadContextLength(model);
        if (contextWindow is not null)
            return contextWindow;

        return model.TryGetProperty("meta", out var meta) // llama.cpp
            ? ProbeHelpers.TryReadPositiveInt32OrFallbackWhenMissing(
                meta, "n_ctx", "n_ctx_train")
            : null;
    }
}
