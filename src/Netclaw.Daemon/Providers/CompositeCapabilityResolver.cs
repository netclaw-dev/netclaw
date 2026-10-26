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
        int? maxOutputTokens = null;
        var windowFromCatalog = false;
        var thinking = ThinkingSupport.Unknown;
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
            {
                contextWindowTokens = result.ContextWindowTokens;
                windowFromCatalog = resolver.ProviderType is null || result.ContextWindowFromCatalog;
            }
            if (maxOutputTokens is null && result.MaxOutputTokens is > 0)
                maxOutputTokens = result.MaxOutputTokens;
            // Unknown is "this source does not say", so a later source may still answer.
            if (thinking.Kind == ThinkingSupportKind.Unknown)
                thinking = result.Thinking;

            _logger.LogDebug(
                "Resolved partial capabilities for {ModelId} via {Resolver}: input={Input}, output={Output}, ctx={Ctx}",
                modelId, resolver.GetType().Name,
                result.InputModalities?.ToString() ?? "null",
                result.OutputModalities?.ToString() ?? "null",
                result.ContextWindowTokens?.ToString() ?? "null");

            // Early-out optimization: the three original fields are filled, stop walking. The newer
            // fields (thinking, model max) do not keep the chain going: a native probe that knows the
            // window is the source of truth for that endpoint, and a catalog call would only add cost.
            if (inputModalities is not null && outputModalities is not null && contextWindowTokens is not null)
                break;
        }

        if (!anyResultProduced)
            return null;

        return new ResolvedModelCapabilities(
            modelId, inputModalities, outputModalities, contextWindowTokens)
        {
            Thinking = thinking,
            MaxOutputTokens = maxOutputTokens,
            ContextWindowFromCatalog = windowFromCatalog,
        };
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

/// <summary>
/// Selects the capability chain of a definition's own provider, so a Compaction model on another
/// endpoint resolves against that endpoint, not against Main's. Each chain is a
/// <see cref="CompositeCapabilityResolver"/>.
/// </summary>
public sealed class ProviderCapabilityResolver : IModelCapabilityResolver
{
    private readonly Dictionary<string, IModelCapabilityResolver> _chainsByProvider;
    private readonly ModelSelection _models;

    public ProviderCapabilityResolver(
        IReadOnlyDictionary<string, IModelCapabilityResolver> chainsByProvider, ModelSelection models)
    {
        // Provider names are configuration keys, which the binder reads without case.
        _chainsByProvider = new Dictionary<string, IModelCapabilityResolver>(chainsByProvider, StringComparer.OrdinalIgnoreCase);
        _models = models;
    }

    public Task<ResolvedModelCapabilities?> ResolveAsync(ModelReference model, CancellationToken ct = default)
        => _chainsByProvider.TryGetValue(model.Provider, out var chain)
            ? chain.ResolveAsync(model.ModelId, ct)
            : Task.FromResult<ResolvedModelCapabilities?>(null);

    /// <summary>
    /// A caller with only a model ID gets the chain of the role definition that names the model. A
    /// model that no role names has no known provider, so its capability is unknown: another
    /// endpoint's chain must not answer for it.
    /// </summary>
    public Task<ResolvedModelCapabilities?> ResolveAsync(string modelId, CancellationToken ct = default)
        => new[] { _models.Main, _models.Fallback, _models.Compaction }
                .FirstOrDefault(m => m is not null && string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            is { } model
            ? ResolveAsync(model, ct)
            : Task.FromResult<ResolvedModelCapabilities?>(null);
}
