// -----------------------------------------------------------------------
// <copyright file="ModelReference.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Points to a specific model on a named provider. Bound from the
/// "Models" configuration section. The <see cref="Provider"/> value
/// must match a key in the "Providers" dictionary.
/// </summary>
public sealed class ModelReference
{
    /// <summary>
    /// The output limit that a call sends when neither the call site nor the definition sets one.
    /// The runtime clamps it per call to the model maximum and to the context window.
    /// </summary>
    public const int DefaultMaxOutputTokens = 32_000;

    public string Provider { get; set; } = "local-ollama";
    public string ModelId { get; set; } = "qwen3:30b";

    /// <summary>
    /// Effective context window size in tokens for this model. When set,
    /// this value clamps the runtime session budget and takes precedence
    /// over provider-reported capability detection.
    /// </summary>
    public int? ContextWindow { get; set; }

    /// <summary>
    /// How this model ID was resolved during onboarding or model selection.
    /// Null for models configured before provenance tracking was added.
    /// </summary>
    public ModelDiscoverySource? Provenance { get; set; }

    /// <summary>
    /// Manual override for input modalities. When set, bypasses all
    /// automated capability detection for this model.
    /// </summary>
    public ModelModality? InputModalities { get; set; }

    /// <summary>
    /// Manual override for output modalities. When set, bypasses all
    /// automated capability detection for this model.
    /// </summary>
    public ModelModality? OutputModalities { get; set; }

    /// <summary>
    /// The output limit in tokens that every call of this model sends, unless the call site sets
    /// its own. Null means the clamped default.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// The thinking level that every call of this model sends, unless the call site sets its own.
    /// Null means the model's default.
    /// </summary>
    public ThinkingLevel? Thinking { get; set; }
}
