// -----------------------------------------------------------------------
// <copyright file="Ds4Descriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.Ds4;

/// <summary>
/// Provider descriptor for DwarfStar (ds4) — antirez's native DeepSeek V4
/// inference engine. ds4 exposes an OpenAI-compatible HTTP API
/// (<c>/v1/chat/completions</c>, <c>/v1/models</c>) with native tool calling.
/// </summary>
/// <remarks>
/// A dedicated type (rather than reusing <c>openai-compatible</c>) exists for one
/// concrete reason: ds4's <c>/v1/models</c> emits OpenRouter-shaped metadata
/// (<c>context_length</c> / <c>top_provider.context_length</c>), which the
/// generic self-hosted resolver — wired for vLLM <c>max_model_len</c> and
/// llama.cpp <c>n_ctx</c> — cannot read. Without it the engine's large
/// context window (its defining feature, backed by compressed KV cache on SSD)
/// would never be auto-detected and compaction would size against a default.
/// The type also carries ds4-specific defaults (endpoint, model ids).
/// </remarks>
public sealed class Ds4Descriptor : IProviderDescriptor
{
    /// <summary>Default ds4-server listen address (see ds4 AGENT.md).</summary>
    public const string DefaultEndpointValue = "http://127.0.0.1:8000";

    private readonly HttpClient _httpClient;

    public Ds4Descriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "ds4";
    public string DisplayName => "DwarfStar (ds4)";
    public string DefaultEndpoint => DefaultEndpointValue;
    public string ModelListingPath => "/v1/models";

    // ds4-server does not enforce authentication; the token Claude Code sends
    // (default "dsv4-local") is ignored server-side. Endpoint-only keeps the
    // CLI from prompting for a credential the engine never checks.
    public IProviderAuth Auth { get; } = new EndpointOnlyAuth();

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            request =>
            {
                // Harmless when present even though ds4 ignores it — keeps the
                // probe forward-compatible if an operator fronts ds4 with a proxy.
                var apiKey = entry.ApiKey?.Value;
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            },
            ParseModels,
            ct,
            timeout: ProbeTimeouts.SelfHosted);
    }

    internal static ProviderProbeResult ParseModels(string json)
        => ProbeHelpers.ParseOpenAiStyleModels(json, ReadContextLength);

    /// <summary>
    /// Reads ds4's OpenRouter-shaped context window: a top-level
    /// <c>context_length</c>, falling back to the nested
    /// <c>top_provider.context_length</c> when the top-level field is absent.
    /// Shared with <see cref="Ds4CapabilityResolver"/>.
    /// </summary>
    internal static int? ReadContextLength(JsonElement model)
    {
        var top = ProbeHelpers.TryReadPositiveInt32(model, "context_length");
        if (top is not null)
            return top;

        return model.TryGetProperty("top_provider", out var topProvider)
            ? ProbeHelpers.TryReadPositiveInt32(topProvider, "context_length")
            : null;
    }
}
