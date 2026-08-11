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
}
