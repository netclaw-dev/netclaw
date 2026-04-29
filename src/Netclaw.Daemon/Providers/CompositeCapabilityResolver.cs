// -----------------------------------------------------------------------
// <copyright file="CompositeCapabilityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Chains multiple <see cref="IModelCapabilityResolver"/> instances in priority
/// order. Returns the first non-null result, or a text-only default if all
/// resolvers return null or fail.
/// </summary>
public sealed class CompositeCapabilityResolver : IModelCapabilityResolver
{
    private static readonly TimeSpan ResolverTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<IModelCapabilityResolver> _resolvers;
    private readonly ILogger<CompositeCapabilityResolver> _logger;

    public CompositeCapabilityResolver(
        IEnumerable<IModelCapabilityResolver> resolvers,
        ILogger<CompositeCapabilityResolver> logger)
    {
        _resolvers = resolvers.ToList();
        _logger = logger;
    }

    public async Task<ResolvedModelCapabilities?> ResolveAsync(
        string modelId, CancellationToken ct = default)
    {
        foreach (var resolver in _resolvers)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ResolverTimeout);

                var result = await resolver.ResolveAsync(modelId, timeoutCts.Token);
                if (result is not null)
                {
                    _logger.LogDebug(
                        "Resolved capabilities for {ModelId} via {Resolver}: input={Input}, output={Output}",
                        modelId, resolver.GetType().Name, result.InputModalities, result.OutputModalities);
                    return result;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("{Resolver} timed out for model {ModelId}, trying next",
                    resolver.GetType().Name, modelId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "{Resolver} failed for model {ModelId}, trying next",
                    resolver.GetType().Name, modelId);
            }
        }

        // All resolvers failed or returned null — default to text-only
        _logger.LogWarning(
            "All capability resolvers failed for model {ModelId}; defaulting to text-only", modelId);
        return new ResolvedModelCapabilities(modelId, ModelModality.Text, ModelModality.Text);
    }
}
