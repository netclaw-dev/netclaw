// -----------------------------------------------------------------------
// <copyright file="ToolIndexContextLayer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Dynamic context layer that advertises the currently discoverable tool surface.
/// Content is computed from the live registry and filtered through the same
/// audience/feature policy used by direct discovery and tool exposure.
/// </summary>
public sealed class ToolIndexContextLayer : IContextLayerProvider
{
    private readonly ToolRegistry _registry;
    private readonly ToolAccessPolicy _policy;

    public ToolIndexContextLayer(ToolRegistry registry, ToolAccessPolicy policy)
    {
        _registry = registry;
        _policy = policy;
    }

    public ContextLayerTiming Timing => ContextLayerTiming.OnceAtStart;

    public string GetContextLayer(TrustAudience audience)
        => _registry.GenerateCompressedIndex(audience, _policy);
}
