namespace Netclaw.Configuration;

/// <summary>
/// Resolved capability metadata for a model.
/// </summary>
public sealed record ResolvedModelCapabilities(
    string ModelId,
    ModelModality InputModalities,
    ModelModality OutputModalities,
    int? ContextWindowTokens = null);

/// <summary>
/// Resolves model capabilities from a specific source (OpenRouter oracle,
/// HuggingFace, etc.). Returns null when the source cannot determine
/// capabilities for the given model.
/// </summary>
public interface IModelCapabilityResolver
{
    Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId,
        CancellationToken ct = default);
}
