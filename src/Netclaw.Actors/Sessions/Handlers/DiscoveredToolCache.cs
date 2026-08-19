// -----------------------------------------------------------------------
// <copyright file="DiscoveredToolCache.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Owns the session's exposed tool list — the always-loaded base tools plus the
/// deferred tools discovered via search_tools — and manages lease-based retention and
/// eviction of the discovered set across turns. Because the cache owns the list
/// it rebuilds, callers seed the base tools once and then drive
/// <see cref="PrepareForNewTurn"/> / <see cref="EvictAll"/> / <see cref="AddIfMissing"/>
/// without passing the list around. Transient and actor-owned; never persisted.
/// </summary>
internal sealed class DiscoveredToolCache
{
    private readonly List<AITool> _availableTools = [];
    private readonly List<string> _order = [];
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private int _baseToolCount;

    /// <summary>
    /// The tools currently exposed to the model this turn: the base tools
    /// followed by any discovered tools with an active lease.
    /// </summary>
    public IReadOnlyList<AITool> AvailableTools => _availableTools;

    /// <summary>
    /// Seed the always-loaded base tools once at session start. Everything added
    /// beyond this set is a discovered tool subject to lease-based eviction.
    /// </summary>
    public void SeedBaseTools(IReadOnlyList<AITool> alwaysLoadedTools)
    {
        _availableTools.Clear();
        _availableTools.AddRange(alwaysLoadedTools);
        _baseToolCount = _availableTools.Count;
    }

    /// <summary>
    /// Prepare the tool set for a new turn: decrement leases, evict expired tools,
    /// and rebuild the available tools list from the cache.
    /// </summary>
    /// <param name="retentionTurns">Configured retention turns (0 or negative disables caching).</param>
    /// <param name="maxCount">Maximum discovered tools to retain.</param>
    /// <param name="registry">Tool registry for resolving tool instances.</param>
    public void PrepareForNewTurn(int retentionTurns, int maxCount, ToolRegistry? registry)
    {
        if (registry is null)
            return;

        if (retentionTurns <= 0 || maxCount <= 0)
        {
            _leases.Clear();
            _order.Clear();
            TrimToBase();
            return;
        }

        if (_leases.Count == 0)
        {
            TrimToBase();
            return;
        }

        var expired = _leases
            .Where(x => x.Value <= 0)
            .Select(x => x.Key)
            .ToList();

        if (expired.Count > 0)
        {
            foreach (var name in expired)
            {
                _leases.Remove(name);
            }

            _order.RemoveAll(name => !_leases.ContainsKey(name));
        }

        RebuildFromCache(registry);

        // Lease countdown happens after this turn's tool set is prepared,
        // so a lease value of N keeps tools available for N future turns.
        foreach (var name in _leases.Keys.ToList())
        {
            _leases[name]--;
        }
    }

    /// <summary>
    /// Remember a discovered deferred tool with a lease for future turns.
    /// </summary>
    public void Remember(string toolName, INetclawTool tool, int leaseTurns, int maxCount)
    {
        if (leaseTurns <= 0 || maxCount <= 0)
            return;

        var lease = Math.Max(1, leaseTurns);
        _leases[toolName] = lease;

        if (!_order.Contains(toolName))
        {
            _order.Add(toolName);
        }

        while (_order.Count > maxCount)
        {
            var evicted = _order[0];
            _order.RemoveAt(0);
            _leases.Remove(evicted);
        }
    }

    /// <summary>
    /// Evict all discovered tools and trim the available tools list back to the
    /// base tools. Used when an LLM call fails to prevent a bad tool set from
    /// poisoning subsequent turns.
    /// </summary>
    public void EvictAll()
    {
        _leases.Clear();
        _order.Clear();
        TrimToBase();
    }

    /// <summary>
    /// Check whether the cache contains a tool with an active lease.
    /// </summary>
    public bool HasTool(string toolName)
    {
        return _leases.TryGetValue(toolName, out var lease) && lease > 0;
    }

    /// <summary>
    /// Add a tool to the exposed list when no <see cref="AIFunctionDeclaration"/> with the
    /// same name is already present. Returns <c>true</c> if it was added.
    /// </summary>
    public bool AddIfMissing(AITool aiTool)
    {
        if (_availableTools.Any(existing =>
            existing is AIFunctionDeclaration current
            && aiTool is AIFunctionDeclaration added
            && current.Name == added.Name))
        {
            return false;
        }

        _availableTools.Add(aiTool);
        return true;
    }

    /// <summary>
    /// Rebuild the available tools list from the discovered tool cache.
    /// </summary>
    private void RebuildFromCache(ToolRegistry registry)
    {
        TrimToBase();

        foreach (var toolName in _order)
        {
            if (!_leases.TryGetValue(toolName, out var lease) || lease <= 0)
                continue;

            var tool = registry.GetByName(toolName);
            if (tool is null)
                continue;

            AddIfMissing(tool.ToAITool());
        }
    }

    private void TrimToBase()
    {
        if (_availableTools.Count > _baseToolCount)
            _availableTools.RemoveRange(_baseToolCount, _availableTools.Count - _baseToolCount);
    }
}
