// -----------------------------------------------------------------------
// <copyright file="CompositeCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Chains multiple <see cref="IModelCapabilityResolver"/> instances and
/// merges partial results across the chain — first non-null wins per
/// field. Provider-native resolvers (non-null <see cref="IModelCapabilityResolver.ProviderType"/>)
/// are skipped when their type does not match the model's active
/// provider; oracle resolvers (null <c>ProviderType</c>) always run.
/// Defaulting for unresolved fields lives at the consumption boundary
/// (<c>ModelCapabilityResolution.ResolveModelCapabilities</c>), not here.
/// </summary>
public sealed class CompositeCapabilityResolver : IModelCapabilityResolver
{
    private static readonly TimeSpan ResolverTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<IModelCapabilityResolver> _resolvers;
    private readonly ILogger<CompositeCapabilityResolver> _logger;
    private readonly Func<string, string?> _activeProviderForModel;

    public CompositeCapabilityResolver(
        IEnumerable<IModelCapabilityResolver> resolvers,
        ILogger<CompositeCapabilityResolver> logger,
        Func<string, string?>? activeProviderForModel = null)
    {
        _resolvers = resolvers.ToList();
        _logger = logger;
        // Default: no filtering. Production callers wire a real lookup from ModelSelection.
        _activeProviderForModel = activeProviderForModel ?? (_ => null);
    }

    public async Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId, CancellationToken ct = default)
    {
        var activeProvider = _activeProviderForModel(modelId);

        // Merge state — each field is filled by the first resolver that
        // returns a non-null value for it.
        ModelModality? inputModalities = null;
        ModelModality? outputModalities = null;
        int? contextWindowTokens = null;
        var anyResultProduced = false;

        foreach (var resolver in _resolvers)
        {
            if (!IsEligible(resolver, activeProvider))
            {
                _logger.LogDebug(
                    "Skipping {Resolver} for model {ModelId}: ProviderType={ResolverProvider} does not match active provider {ActiveProvider}",
                    resolver.GetType().Name, modelId, resolver.ProviderType, activeProvider);
                continue;
            }

            ResolvedModelCapabilities? result;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ResolverTimeout);

                result = await resolver.ResolveAsync(modelId, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("{Resolver} timed out for model {ModelId}, continuing chain",
                    resolver.GetType().Name, modelId);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "{Resolver} failed for model {ModelId}, continuing chain",
                    resolver.GetType().Name, modelId);
                continue;
            }

            if (result is null)
                continue;

            anyResultProduced = true;
            inputModalities ??= result.InputModalities;
            outputModalities ??= result.OutputModalities;
            // Context windows are positive-only. A 0 from provider metadata is an
            // unknown sentinel, not a resolved value, and must not block a later
            // resolver from supplying the real limit.
            if (contextWindowTokens is null && result.ContextWindowTokens is > 0)
                contextWindowTokens = result.ContextWindowTokens;

            _logger.LogDebug(
                "Resolved partial capabilities for {ModelId} via {Resolver}: input={Input}, output={Output}, ctx={Ctx}",
                modelId, resolver.GetType().Name,
                result.InputModalities?.ToString() ?? "null",
                result.OutputModalities?.ToString() ?? "null",
                result.ContextWindowTokens?.ToString() ?? "null");

            // Early-out optimization: every field filled, stop walking.
            if (inputModalities is not null && outputModalities is not null && contextWindowTokens is not null)
                break;
        }

        if (!anyResultProduced)
            return null;

        return new ResolvedModelCapabilities(
            modelId, inputModalities, outputModalities, contextWindowTokens);
    }

    private static bool IsEligible(IModelCapabilityResolver resolver, string? activeProvider)
    {
        // Oracles (null ProviderType) always run.
        if (resolver.ProviderType is null)
            return true;
        // No active provider known → can't filter → keep current behavior (run all).
        if (activeProvider is null)
            return true;
        return string.Equals(resolver.ProviderType, activeProvider, StringComparison.OrdinalIgnoreCase);
    }
}
