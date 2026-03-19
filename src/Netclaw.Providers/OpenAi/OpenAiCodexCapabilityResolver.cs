using Netclaw.Configuration;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Resolves model capabilities for OpenAI Codex models from the static
/// <see cref="OpenAiDescriptor.CuratedModels"/> catalog. No network
/// calls — Codex OAuth tokens cannot query <c>/v1/models</c>.
/// </summary>
public sealed class OpenAiCodexCapabilityResolver : IModelCapabilityResolver
{
    private static readonly Dictionary<string, ResolvedModelCapabilities> Catalog =
        OpenAiDescriptor.CuratedModels.ToDictionary(
            m => m.ModelId,
            m => new ResolvedModelCapabilities(
                m.ModelId,
                m.InputModalities,
                m.OutputModalities,
                m.ContextWindowTokens));

    public Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId, CancellationToken ct = default)
    {
        Catalog.TryGetValue(modelId, out var result);
        return Task.FromResult(result);
    }
}
