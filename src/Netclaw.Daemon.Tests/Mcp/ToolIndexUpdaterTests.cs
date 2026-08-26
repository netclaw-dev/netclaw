// -----------------------------------------------------------------------
// <copyright file="ToolIndexUpdaterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class ToolIndexUpdaterTests
{
    [Fact]
    public async Task StartAsync_with_no_user_facing_agents_sets_actionable_discovery_layer()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        var registry = new ToolRegistry();
        var memoryLayer = new MemoryIndexContextLayer();
        var subAgentRegistry = new SubAgentDefinitionRegistry();
        var loader = new FileSubAgentDefinitionLoader(paths, NullLogger<FileSubAgentDefinitionLoader>.Instance);
        var subAgentLayer = new SubAgentDiscoveryContextLayer(new SubAgentConfig(), subAgentRegistry, loader, paths);
        var writer = new McpShadowCatalogWriter(paths, registry, NullLogger<McpShadowCatalogWriter>.Instance);

        var updater = new ToolIndexUpdater(
            paths,
            writer,
            registry,
            memoryLayer,
            subAgentRegistry,
            loader,
            subAgentSpawner: null!,
            new SubAgentConfig(),
            NullLoggerFactory.Instance);

        await updater.StartAsync(TestContext.Current.CancellationToken);

        var discovery = subAgentLayer.GetContextLayer(TrustAudience.Personal);
        Assert.False(string.IsNullOrWhiteSpace(discovery));
        Assert.Contains("available-subagents", discovery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(paths.AgentsDirectory, discovery, StringComparison.Ordinal);

        Assert.Contains("Sub-agents inherit the parent audience policy", discovery, StringComparison.Ordinal);
        foreach (var tool in SubAgentToolPolicy.GetDeniedSubAgentTools())
            Assert.Contains(tool, discovery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_keeps_public_tool_index_filtered_from_hidden_capabilities()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        var config = new ToolConfig();
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Public,
                TrustAudience.Public,
                ShellExecutionMode.Off,
                UsedStrictFallback: true),
            shellCommandPolicy: new ShellCommandPolicy(),
            toolPathPolicy: new ToolPathPolicy([]),
            featureGates: new FeatureGates(SubAgentsEnabled: false, SchedulingEnabled: false));
        var registry = new ToolRegistry();
        registry.Register(AIFunctionFactory.Create(() => "ok", "file_read"), "file");
        registry.Register(AIFunctionFactory.Create(() => "ok", "set_reminder"), "builtin");
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create(() => "ok", "search", "Search memory"),
            "memorizer",
            "search"));

        var memoryLayer = new MemoryIndexContextLayer();
        var toolIndexLayer = new ToolIndexContextLayer(registry, policy);
        var subAgentRegistry = new SubAgentDefinitionRegistry();
        var loader = new FileSubAgentDefinitionLoader(paths, NullLogger<FileSubAgentDefinitionLoader>.Instance);
        var writer = new McpShadowCatalogWriter(paths, registry, NullLogger<McpShadowCatalogWriter>.Instance);

        var updater = new ToolIndexUpdater(
            paths,
            writer,
            registry,
            memoryLayer,
            subAgentRegistry,
            loader,
            subAgentSpawner: null!,
            new SubAgentConfig { Enabled = false },
            NullLoggerFactory.Instance);

        await updater.StartAsync(TestContext.Current.CancellationToken);

        var publicIndex = toolIndexLayer.GetContextLayer(TrustAudience.Public);
        Assert.Contains("[deferred first-party tools", publicIndex);
        Assert.Contains("file_read:", publicIndex);
        Assert.DoesNotContain("set_reminder", publicIndex);
        Assert.DoesNotContain("memorizer", publicIndex);
    }
}
