using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Tracks MCP tools discovered via search_tools across turns, managing
/// lease-based retention and eviction.
/// </summary>
internal sealed class DiscoveredToolCache
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);

    /// <summary>
    /// Prepare the tool cache for a new turn: decrement leases, evict expired tools,
    /// and rebuild the available tools list from the cache.
    /// </summary>
    /// <param name="availableTools">The mutable tools list owned by the actor.</param>
    /// <param name="baseToolCount">Count of always-loaded tools (dynamic tools start after this index).</param>
    /// <param name="retentionTurns">Configured retention turns (0 or negative disables caching).</param>
    /// <param name="maxCount">Maximum discovered tools to retain.</param>
    /// <param name="registry">Tool registry for resolving tool instances.</param>
    public void PrepareForNewTurn(
        List<AITool> availableTools,
        int baseToolCount,
        int retentionTurns,
        int maxCount,
        ToolRegistry? registry)
    {
        if (registry is null)
            return;

        if (retentionTurns <= 0 || maxCount <= 0)
        {
            _leases.Clear();
            _order.Clear();
            TrimToBase(availableTools, baseToolCount);
            return;
        }

        if (_leases.Count == 0)
        {
            TrimToBase(availableTools, baseToolCount);
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

        RebuildFromCache(availableTools, baseToolCount, registry);

        // Lease countdown happens after this turn's tool set is prepared,
        // so a lease value of N keeps tools available for N future turns.
        foreach (var name in _leases.Keys.ToList())
        {
            _leases[name]--;
        }
    }

    /// <summary>
    /// Remember a discovered MCP tool with a lease for future turns.
    /// </summary>
    public void Remember(string toolName, INetclawTool tool, int leaseTurns, int maxCount)
    {
        if (tool is not McpToolAdapter)
            return;

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
    /// Evict all discovered tools and trim the available tools list back to base tools.
    /// Used when an LLM call fails to prevent a bad tool set from poisoning subsequent turns.
    /// </summary>
    public void EvictAll(List<AITool> availableTools, int baseToolCount)
    {
        _leases.Clear();
        _order.Clear();
        TrimToBase(availableTools, baseToolCount);
    }

    /// <summary>
    /// Check whether the cache contains a tool with an active lease.
    /// </summary>
    public bool HasTool(string toolName)
    {
        return _leases.TryGetValue(toolName, out var lease) && lease > 0;
    }

    /// <summary>
    /// Rebuild the available tools list from the discovered tool cache.
    /// </summary>
    private void RebuildFromCache(List<AITool> availableTools, int baseToolCount, ToolRegistry registry)
    {
        TrimToBase(availableTools, baseToolCount);

        foreach (var toolName in _order)
        {
            if (!_leases.TryGetValue(toolName, out var lease) || lease <= 0)
                continue;

            var tool = registry.GetByName(toolName);
            if (tool is null)
                continue;

            AddIfMissing(availableTools, toolName, tool.ToAITool());
        }
    }

    private static void TrimToBase(List<AITool> availableTools, int baseToolCount)
    {
        if (availableTools.Count > baseToolCount)
            availableTools.RemoveRange(baseToolCount, availableTools.Count - baseToolCount);
    }

    private static void AddIfMissing(List<AITool> availableTools, string toolName, AITool aiTool)
    {
        if (availableTools.Any(existing =>
            existing is AIFunction ef && aiTool is AIFunction nf && ef.Name == nf.Name))
        {
            return;
        }

        availableTools.Add(aiTool);
    }
}
