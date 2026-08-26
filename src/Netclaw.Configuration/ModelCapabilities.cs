// -----------------------------------------------------------------------
// <copyright file="ModelCapabilities.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Runtime-derived model properties resolved from the capability detection
/// pipeline at startup. Separate from <see cref="SessionConfig"/> because
/// these values come from model selection and provider introspection, not
/// from operator configuration in the Session config section.
/// </summary>
public sealed record ModelCapabilities
{
    /// <summary>
    /// The model identifier (e.g., "qwen3:30b", "claude-sonnet-4-20250514").
    /// Populated from <see cref="ModelSelection.Main"/> at startup.
    /// </summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>
    /// Maximum context window size in tokens for the configured model.
    /// Used to determine when compaction should trigger.
    /// </summary>
    public int ContextWindowTokens { get; init; } = 32_768;

    /// <summary>
    /// Content types the configured model accepts as input.
    /// Defaults to <see cref="ModelModality.Text"/> when capabilities
    /// have not been resolved.
    /// </summary>
    public ModelModality InputModalities { get; init; } = ModelModality.Text;

    /// <summary>
    /// Content types the configured model can produce as output.
    /// Defaults to <see cref="ModelModality.Text"/> when capabilities
    /// have not been resolved.
    /// </summary>
    public ModelModality OutputModalities { get; init; } = ModelModality.Text;

    /// <summary>
    /// Optional model ID for compaction summarization.
    /// When set, compaction LLM calls use this model (typically cheaper/faster)
    /// instead of the primary session model. Resolved from
    /// <see cref="ModelSelection.Compaction"/> at startup.
    /// </summary>
    public string? CompactionModelId { get; init; }

    /// <summary>
    /// Effective token limit at which compaction fires, given a threshold ratio.
    /// </summary>
    public int CompactionTokenLimit(double threshold) => (int)(ContextWindowTokens * threshold);
}
