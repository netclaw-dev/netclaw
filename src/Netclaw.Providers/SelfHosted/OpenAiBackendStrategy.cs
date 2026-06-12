// -----------------------------------------------------------------------
// <copyright file="OpenAiBackendStrategy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Pre-parsed probe results shared across strategy implementations. The
/// resolver owns the underlying <see cref="JsonDocument"/> lifetime, so
/// <see cref="JsonElement"/> references here are valid only for the
/// duration of one <c>ResolveAsync</c> call. <see cref="PropsRoot"/> is
/// null when the backend has no <c>/props</c> endpoint (e.g. vLLM 404).
/// </summary>
internal sealed record BackendProbe(string ModelId, JsonElement ModelsRoot, JsonElement? PropsRoot)
{
    /// <summary>
    /// Locates the <c>/v1/models</c> entry matching <see cref="ModelId"/>
    /// by case-insensitive <c>id</c> comparison. Returns false when no
    /// entry matches or the response shape is unexpected.
    /// </summary>
    public bool TryFindModelEntry(out JsonElement entry)
    {
        entry = default;
        if (!ModelsRoot.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var model in data.EnumerateArray())
        {
            if (model.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), ModelId, StringComparison.OrdinalIgnoreCase))
            {
                entry = model;
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Backend-specific parser for an OpenAI-compatible endpoint. The
/// resolver enumerates strategies in priority order and uses the first
/// whose <see cref="Matches"/> predicate is true.
/// </summary>
internal interface IOpenAiBackendStrategy
{
    /// <summary>Diagnostic name written to logs when a backend is detected.</summary>
    string Name { get; }

    bool Matches(BackendProbe probe);

    ResolvedModelCapabilities? Parse(BackendProbe probe);
}

/// <summary>
/// vLLM-specific strategy. Recognizes vLLM via either <c>owned_by: "vllm"</c>
/// on a <c>/v1/models</c> entry or the presence of a top-level
/// <c>max_model_len</c> on the model entry combined with <c>/props</c>
/// returning non-200. Parses <c>max_model_len</c> as the context window
/// and leaves modalities null so downstream resolvers (HuggingFace) can
/// fill them — vLLM exposes no modality field anywhere.
/// </summary>
internal sealed class VllmBackendStrategy : IOpenAiBackendStrategy
{
    public string Name => "vllm";

    public bool Matches(BackendProbe probe)
    {
        if (!probe.TryFindModelEntry(out var model))
            return false;

        if (model.TryGetProperty("owned_by", out var ownedBy) &&
            ownedBy.ValueKind == JsonValueKind.String &&
            string.Equals(ownedBy.GetString(), "vllm", StringComparison.OrdinalIgnoreCase))
            return true;

        // /props 404 + max_model_len present is also a strong vLLM signal.
        return probe.PropsRoot is null &&
               model.TryGetProperty("max_model_len", out var mml) &&
               mml.ValueKind == JsonValueKind.Number;
    }

    public ResolvedModelCapabilities? Parse(BackendProbe probe)
    {
        if (!probe.TryFindModelEntry(out var model))
            return null;

        var contextWindow = ProbeHelpers.TryReadPositiveInt32(model, "max_model_len");

        // vLLM exposes no modality information; null fields signal that
        // downstream resolvers in the chain (HuggingFace) should fill in.
        return new ResolvedModelCapabilities(probe.ModelId, null, null, contextWindow);
    }
}

/// <summary>
/// llama.cpp-specific strategy. Recognizes llama.cpp via either a
/// successful <c>/props</c> response or a <c>meta.n_ctx</c>/<c>meta.n_ctx_train</c>
/// field on a <c>/v1/models</c> entry. Prefers <c>/props.default_generation_settings.n_ctx</c>
/// for the runtime-effective context window, and reads
/// <c>/props.modalities.vision</c> for input modality.
/// </summary>
internal sealed class LlamaCppBackendStrategy : IOpenAiBackendStrategy
{
    public string Name => "llama.cpp";

    public bool Matches(BackendProbe probe)
    {
        if (probe.PropsRoot is not null)
            return true;

        if (!probe.TryFindModelEntry(out var model))
            return false;

        return TryReadModelMetaContext(model) is not null;
    }

    public ResolvedModelCapabilities? Parse(BackendProbe probe)
    {
        // Start with what /v1/models tells us about context window.
        int? contextWindow = null;
        if (probe.TryFindModelEntry(out var model))
            contextWindow = TryReadModelMetaContext(model);

        // /props overrides with the runtime-effective n_ctx when it is real.
        // llama.cpp router mode returns a router-level /props body with n_ctx=0
        // and no per-model modality evidence. That response must not override
        // model metadata or block later resolvers from detecting image support.
        ModelModality? inputModalities = null;
        ModelModality? outputModalities = null;
        if (probe.PropsRoot is JsonElement props)
        {
            if (!IsRouterProps(props))
            {
                contextWindow = TryReadPropsContext(props) ?? contextWindow;

                if (TryReadInputModalities(props) is { } detectedInputModalities)
                {
                    inputModalities = detectedInputModalities;
                    outputModalities = ModelModality.Text;
                }
            }
        }

        return new ResolvedModelCapabilities(
            probe.ModelId, inputModalities, outputModalities, contextWindow);
    }

    private static int? TryReadModelMetaContext(JsonElement model)
    {
        if (!model.TryGetProperty("meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object)
            return null;

        return ProbeHelpers.TryReadPositiveInt32OrFallbackWhenMissing(
            meta, "n_ctx", "n_ctx_train");
    }

    private static int? TryReadPropsContext(JsonElement props)
    {
        if (!props.TryGetProperty("default_generation_settings", out var dgs) ||
            dgs.ValueKind != JsonValueKind.Object)
            return null;

        return ProbeHelpers.TryReadPositiveInt32(dgs, "n_ctx") ??
               (dgs.TryGetProperty("params", out var parameters)
                   ? ProbeHelpers.TryReadPositiveInt32(parameters, "n_ctx")
                   : null);
    }

    private static ModelModality? TryReadInputModalities(JsonElement props)
    {
        if (!props.TryGetProperty("modalities", out var modalities) ||
            modalities.ValueKind != JsonValueKind.Object ||
            !modalities.TryGetProperty("vision", out var vision) ||
            vision.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;

        return vision.GetBoolean()
            ? ModelModality.Text | ModelModality.Image
            : ModelModality.Text;
    }

    private static bool IsRouterProps(JsonElement props)
    {
        // Router-level /props describes the router process, not the selected model.
        // Do not let its placeholders become authoritative per-model capabilities.
        return props.TryGetProperty("role", out var role) &&
               role.ValueKind == JsonValueKind.String &&
               string.Equals(role.GetString(), "router", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// DwarfStar (ds4) strategy — antirez's native DeepSeek V4 inference engine.
/// Recognized via <c>owned_by: "ds4.c"</c> on the <c>/v1/models</c> entry.
/// ds4 emits OpenRouter-shaped metadata: the context window lives in
/// <c>context_length</c> / <c>top_provider.context_length</c>, fields neither
/// the vLLM nor llama.cpp strategies read. Without this strategy the engine's
/// large context window (its defining feature, backed by compressed KV cache
/// on SSD) would never be auto-detected and compaction would size against a
/// default. Ordered before llama.cpp so a proxy that happens to serve
/// <c>/props</c> in front of ds4 cannot steal the match.
/// </summary>
internal sealed class Ds4BackendStrategy : IOpenAiBackendStrategy
{
    public string Name => "ds4";

    public bool Matches(BackendProbe probe)
    {
        if (!probe.TryFindModelEntry(out var model))
            return false;

        return model.TryGetProperty("owned_by", out var ownedBy) &&
               ownedBy.ValueKind == JsonValueKind.String &&
               string.Equals(ownedBy.GetString(), "ds4.c", StringComparison.OrdinalIgnoreCase);
    }

    public ResolvedModelCapabilities? Parse(BackendProbe probe)
    {
        if (!probe.TryFindModelEntry(out var model))
            return null;

        // ds4 serves text-only DeepSeek V4 models; report modalities explicitly
        // (its model ids are not HuggingFace ids, so downstream resolvers would
        // have nothing useful to fill in).
        return new ResolvedModelCapabilities(
            probe.ModelId, ModelModality.Text, ModelModality.Text, ReadContextLength(model));
    }

    /// <summary>
    /// Reads the OpenRouter-shaped context window: a top-level
    /// <c>context_length</c>, falling back to the nested
    /// <c>top_provider.context_length</c> when the top-level field is absent.
    /// Shared with <see cref="OpenAiCompatibleDescriptor"/> probe parsing.
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

/// <summary>
/// Last-resort fallback when no backend-specific signals are present.
/// Returns the model id with null fields so downstream resolvers
/// (OpenRouter oracle, HuggingFace) have the chance to fill in.
/// Always matches.
/// </summary>
internal sealed class GenericOpenAiBackendStrategy : IOpenAiBackendStrategy
{
    public string Name => "generic-openai";

    public bool Matches(BackendProbe probe) => true;

    public ResolvedModelCapabilities? Parse(BackendProbe probe)
        => new(probe.ModelId, null, null, null);
}
