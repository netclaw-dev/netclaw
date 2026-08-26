// -----------------------------------------------------------------------
// <copyright file="ModelCapabilityResolution.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

internal static class ModelCapabilityResolution
{
    /// <summary>
    /// Resolve runtime model capabilities from the model reference configuration
    /// and detected capabilities. Returns a <see cref="ModelCapabilities"/> instance
    /// with all fields populated (never null).
    /// </summary>
    public static ModelCapabilities ResolveModelCapabilities(
        ModelSelection models,
        ResolvedModelCapabilities? detected,
        int defaultContextWindow = 32_768,
        ILogger? logger = null)
    {
        var model = models.Main;
        // Final safety net: provider parsers should normalize non-positive context
        // metadata to null, but keep runtime capabilities valid even if an older
        // or custom resolver reports llama.cpp-style n_ctx=0 as a sentinel.
        var detectedContextWindow = detected?.ContextWindowTokens is > 0
            ? detected.ContextWindowTokens
            : null;

        if (model.ContextWindow is int configuredContextWindow
            && detectedContextWindow is int detectedWindow
            && configuredContextWindow > detectedWindow)
        {
            // The configured window exceeds what the provider reports, but
            // provider-reported context windows are frequently wrong or absent
            // (router placeholders, n_ctx=0 sentinels, llama.cpp started with a
            // larger --ctx-size than it advertises). Refusing to boot on that
            // signal takes down every session over a number we can't trust, so
            // honor the operator's value and warn. If it really is too large the
            // provider rejects the oversized request at runtime and the session
            // compacts-and-retries (see LlmSessionActor), surfacing an actionable
            // per-turn error rather than a daemon-wide startup failure.
            logger?.LogWarning(
                "Models:Main:ContextWindow ({ConfiguredContextWindow}) exceeds the " +
                "provider-reported effective context window ({DetectedContextWindow}). " +
                "Using the configured value; if requests are rejected at runtime, reduce " +
                "ContextWindow or adjust the provider runtime settings.",
                configuredContextWindow, detectedWindow);
        }

        var inputModalities = model.InputModalities ?? detected?.InputModalities ?? ModelModality.Text;
        var outputModalities = model.OutputModalities ?? detected?.OutputModalities ?? ModelModality.Text;
        var contextWindow = model.ContextWindow ?? detectedContextWindow ?? defaultContextWindow;

        return new ModelCapabilities
        {
            ModelId = model.ModelId,
            ContextWindowTokens = contextWindow,
            InputModalities = inputModalities,
            OutputModalities = outputModalities,
            CompactionModelId = models.Compaction?.ModelId,
        };
    }
}
