// -----------------------------------------------------------------------
// <copyright file="IModelCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Resolved capability metadata for a model. Any field may be null when the
/// resolver could not determine that field; the composite resolver merges
/// partial results across the chain by first-non-null-wins.
/// </summary>
public sealed record ResolvedModelCapabilities(
    string ModelId,
    ModelModality? InputModalities,
    ModelModality? OutputModalities,
    int? ContextWindowTokens = null)
{
    /// <summary>Whether the model thinks, and the levels that it accepts.</summary>
    public ThinkingSupport Thinking { get; init; } = ThinkingSupport.Unknown;

    /// <summary>The model's maximum output in tokens, when a source reports it.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// True when <see cref="ContextWindowTokens"/> comes from a cross-provider catalog, not from the
    /// endpoint that serves the model. A catalog window describes another host, so the default output
    /// limit of a self-hosted endpoint does not clamp to it.
    /// </summary>
    public bool ContextWindowFromCatalog { get; init; }
}

/// <summary>
/// Resolves model capabilities from a specific source (provider-native API,
/// OpenRouter oracle, HuggingFace, etc.). Returns null when the source cannot
/// determine any capability for the given model. Returning a record with null
/// fields is allowed and indicates a partial result that downstream resolvers
/// can supplement.
/// </summary>
public interface IModelCapabilityResolver
{
    /// <summary>
    /// Provider type this resolver speaks for (e.g., "openai", "ollama",
    /// "openai-compatible"). When null the resolver is treated as a
    /// cross-provider oracle and is always eligible regardless of the
    /// active model's provider.
    /// </summary>
    string? ProviderType => null;

    Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the capabilities of one model definition against its own provider. A resolver that
    /// speaks for one endpoint ignores the provider name; the daemon resolver selects the chain of
    /// <see cref="ModelReference.Provider"/>.
    /// </summary>
    Task<ResolvedModelCapabilities?> ResolveAsync(
        ModelReference model,
        CancellationToken ct = default)
        => ResolveAsync(model.ModelId, ct);
}
