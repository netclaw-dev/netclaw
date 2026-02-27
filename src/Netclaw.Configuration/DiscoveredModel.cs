namespace Netclaw.Configuration;

/// <summary>
/// Metadata about a model discovered from a provider at runtime.
/// All fields except <see cref="ModelId"/> are optional — availability
/// depends on what the provider's discovery API returns.
/// </summary>
public sealed record DiscoveredModel
{
    /// <summary>Model identifier as used in API calls.</summary>
    public required string ModelId { get; init; }

    /// <summary>Maximum context window size in tokens, if known.</summary>
    public int? ContextWindowTokens { get; init; }

    /// <summary>Number of model parameters (e.g., 30B = 30_000_000_000).</summary>
    public long? ParameterCount { get; init; }

    /// <summary>Cost per million input tokens in USD, if known.</summary>
    public decimal? CostPerMillionInputTokens { get; init; }

    /// <summary>Cost per million output tokens in USD, if known.</summary>
    public decimal? CostPerMillionOutputTokens { get; init; }
}
