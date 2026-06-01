// -----------------------------------------------------------------------
// <copyright file="ModelCapabilityResolution.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        int defaultContextWindow = 32_768)
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
            throw new InvalidOperationException(
                $"Models:Main:ContextWindow ({configuredContextWindow}) exceeds the " +
                $"provider-reported effective context window ({detectedWindow}). " +
                "Reduce the configured ContextWindow or adjust the provider runtime settings.");
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
