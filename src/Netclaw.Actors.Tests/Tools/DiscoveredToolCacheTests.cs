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

        // Register and load 3 MCP tools
        RegisterAndRemember(registry, cache, "server", "tool_a", retentionTurns: 3, maxCount: 12);
        RegisterAndRemember(registry, cache, "server", "tool_b", retentionTurns: 3, maxCount: 12);
        RegisterAndRemember(registry, cache, "server", "tool_c", retentionTurns: 3, maxCount: 12);

        Assert.Equal(3, cache.AvailableTools.Count);
        Assert.True(cache.HasTool("server/tool_a"));
        Assert.True(cache.HasTool("server/tool_b"));
        Assert.True(cache.HasTool("server/tool_c"));

        // Evict all — simulates compaction reset
        cache.EvictAll();

        Assert.Empty(cache.AvailableTools);
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
        cache.SeedBaseTools([baseTool1, baseTool2]);

        RegisterAndRemember(registry, cache, "notion", "search", retentionTurns: 3, maxCount: 12);

        Assert.Equal(3, cache.AvailableTools.Count);

        cache.EvictAll();

        Assert.Equal(2, cache.AvailableTools.Count);
        Assert.Contains(baseTool1, cache.AvailableTools);
        Assert.Contains(baseTool2, cache.AvailableTools);
    }

    [Fact]
    public void PrepareForNewTurn_AfterEvictAll_DoesNotRestoreEvictedTools()
    {
        var registry = new ToolRegistry();
        var cache = new DiscoveredToolCache();

        RegisterAndRemember(registry, cache, "notion", "search", retentionTurns: 5, maxCount: 12);
        Assert.Single(cache.AvailableTools);

        cache.EvictAll();
        Assert.Empty(cache.AvailableTools);

        // Next turn — evicted tools should NOT come back
        cache.PrepareForNewTurn(retentionTurns: 5, maxCount: 12, registry);
        Assert.Empty(cache.AvailableTools);
    }

    [Fact]
    public void Deferred_first_party_tool_uses_the_same_lease_and_eviction_path()
    {
        var registry = new ToolRegistry();
        var cache = new DiscoveredToolCache();
        var function = AIFunctionFactory.Create(() => "result", "set_reminder", "Schedule a reminder");
        registry.Register(function, "builtin");
        var tool = Assert.IsAssignableFrom<Netclaw.Tools.INetclawTool>(registry.GetByName("set_reminder"));

        cache.Remember(tool.Name, tool, leaseTurns: 2, maxCount: 12);
        cache.AddIfMissing(tool.ToAITool());

        Assert.True(cache.HasTool("set_reminder"));
        Assert.Contains(
            cache.AvailableTools,
            static candidate => candidate is AIFunction functionTool && functionTool.Name == "set_reminder");

        cache.EvictAll();

        Assert.False(cache.HasTool("set_reminder"));
        Assert.Empty(cache.AvailableTools);
    }

    [Fact]
    public void AddIfMissing_deduplicates_source_generated_tool_declarations()
    {
        var registry = new ToolRegistry();
        var tool = new LoadToolTool(
            registry,
            TestToolAccessPolicy.Create(new Netclaw.Configuration.ToolConfig()))
            .ToAITool();
        var cache = new DiscoveredToolCache();
        cache.SeedBaseTools([tool]);

        var added = cache.AddIfMissing(tool);

        Assert.False(added);
        Assert.Single(cache.AvailableTools);
    }

    private static McpToolAdapter RegisterAndRemember(
        ToolRegistry registry,
        DiscoveredToolCache cache,
        string serverName,
        string toolName,
        int retentionTurns,
        int maxCount)
    {
        var fake = AIFunctionFactory.Create(() => "result", toolName, $"Description for {toolName}");
        var adapter = new McpToolAdapter(fake, serverName, toolName);
        registry.Register(adapter);
        cache.Remember(adapter.Name, adapter, retentionTurns, maxCount);
        cache.AddIfMissing(adapter.ToAITool());
        return adapter;
    }
}
