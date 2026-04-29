// -----------------------------------------------------------------------
// <copyright file="DiscoveredToolCacheTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class DiscoveredToolCacheTests
{
    [Fact]
    public void EvictAll_ClearsAllDiscoveredTools()
    {
        var registry = new ToolRegistry();
        var cache = new DiscoveredToolCache();
        var availableTools = new List<AITool>();

        // Register and load 3 MCP tools
        var tool1 = RegisterAndRemember(registry, cache, availableTools, "server", "tool_a", retentionTurns: 3, maxCount: 12);
        var tool2 = RegisterAndRemember(registry, cache, availableTools, "server", "tool_b", retentionTurns: 3, maxCount: 12);
        var tool3 = RegisterAndRemember(registry, cache, availableTools, "server", "tool_c", retentionTurns: 3, maxCount: 12);

        Assert.Equal(3, availableTools.Count);
        Assert.True(cache.HasTool("server/tool_a"));
        Assert.True(cache.HasTool("server/tool_b"));
        Assert.True(cache.HasTool("server/tool_c"));

        // Evict all — simulates compaction reset
        cache.EvictAll(availableTools, baseToolCount: 0);

        Assert.Empty(availableTools);
        Assert.False(cache.HasTool("server/tool_a"));
        Assert.False(cache.HasTool("server/tool_b"));
        Assert.False(cache.HasTool("server/tool_c"));
    }

    [Fact]
    public void EvictAll_PreservesBaseTools()
    {
        var registry = new ToolRegistry();
        var cache = new DiscoveredToolCache();

        // Simulate 2 base tools + 1 discovered tool
        var baseTool1 = AIFunctionFactory.Create(() => "result", "search_tools");
        var baseTool2 = AIFunctionFactory.Create(() => "result", "load_tool");
        var availableTools = new List<AITool> { baseTool1, baseTool2 };
        var baseToolCount = 2;

        RegisterAndRemember(registry, cache, availableTools, "notion", "search", retentionTurns: 3, maxCount: 12);

        Assert.Equal(3, availableTools.Count);

        cache.EvictAll(availableTools, baseToolCount);

        Assert.Equal(2, availableTools.Count);
        Assert.Contains(baseTool1, availableTools);
        Assert.Contains(baseTool2, availableTools);
    }

    [Fact]
    public void PrepareForNewTurn_AfterEvictAll_DoesNotRestoreEvictedTools()
    {
        var registry = new ToolRegistry();
        var cache = new DiscoveredToolCache();
        var availableTools = new List<AITool>();

        RegisterAndRemember(registry, cache, availableTools, "notion", "search", retentionTurns: 5, maxCount: 12);
        Assert.Single(availableTools);

        cache.EvictAll(availableTools, baseToolCount: 0);
        Assert.Empty(availableTools);

        // Next turn — evicted tools should NOT come back
        cache.PrepareForNewTurn(availableTools, baseToolCount: 0, retentionTurns: 5, maxCount: 12, registry);
        Assert.Empty(availableTools);
    }

    private static McpToolAdapter RegisterAndRemember(
        ToolRegistry registry,
        DiscoveredToolCache cache,
        List<AITool> availableTools,
        string serverName,
        string toolName,
        int retentionTurns,
        int maxCount)
    {
        var fake = AIFunctionFactory.Create(() => "result", toolName, $"Description for {toolName}");
        var adapter = new McpToolAdapter(fake, serverName, toolName);
        registry.Register(adapter);
        cache.Remember(adapter.Name, adapter, retentionTurns, maxCount);
        availableTools.Add(adapter.ToAITool());
        return adapter;
    }
}
