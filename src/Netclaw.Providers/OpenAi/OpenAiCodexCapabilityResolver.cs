// -----------------------------------------------------------------------
// <copyright file="OpenAiCodexCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Resolves the context window reported by the configured OpenAI Codex OAuth provider.
/// </summary>
public sealed class OpenAiCodexCapabilityResolver : IModelCapabilityResolver
{
    private readonly IConfiguredProviderProbe _probe;
    private readonly IReadOnlyDictionary<string, ProviderEntry> _providers;
    private readonly IReadOnlyDictionary<string, string> _providerNamesByModel;

    public OpenAiCodexCapabilityResolver(
        IConfiguredProviderProbe probe,
        IReadOnlyDictionary<string, ProviderEntry> providers,
        IReadOnlyDictionary<string, string> providerNamesByModel)
    {
        _probe = probe;
        _providers = providers;
        _providerNamesByModel = providerNamesByModel;
    }

    public string? ProviderType => "openai";

    public async Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId,
        CancellationToken ct = default)
    {
        if (!_providerNamesByModel.TryGetValue(modelId, out var providerName)
            || !_providers.TryGetValue(providerName, out var provider)
            || !string.Equals(provider.Type, "openai", StringComparison.OrdinalIgnoreCase)
            || provider.AuthMethod is not (AuthMethod.OAuthDevice or AuthMethod.OAuthPkce))
        {
            return null;
        }

        var result = await _probe.ProbeConfiguredAsync(providerName, provider, ct);
        if (!result.Success)
            return null;

        var discoveredModel = result.Models.FirstOrDefault(model =>
            string.Equals(model.ModelId.Value, modelId, StringComparison.OrdinalIgnoreCase));
        var contextWindow = discoveredModel?.ContextWindowTokens;
        return contextWindow is > 0
            ? new ResolvedModelCapabilities(modelId, null, null, contextWindow)
            : null;
    }
}
