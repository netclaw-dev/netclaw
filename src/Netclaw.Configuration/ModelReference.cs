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
    /// Override for the effective context window size in tokens.
    /// Useful when parallelism factors reduce the actual usable window
    /// (e.g., Lemonade with parallelism=4 reduces a 262,144-token window to ~65,536).
    /// If not specified, the provider-reported window is used.
    /// Must be >= 8192.
    /// </summary>
    public int? ContextWindowOverride { get; set; }

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
