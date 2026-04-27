using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class ToolIndexUpdaterTests
{
    [Fact]
    public async Task StartAsync_with_no_user_facing_agents_sets_actionable_discovery_layer()
    {
        var tempDir = CreateTempDir();
        try
        {
            var paths = new NetclawPaths(tempDir);
            paths.EnsureDirectoriesExist();

            var registry = new ToolRegistry();
            var memoryLayer = new MemoryIndexContextLayer();
            var subAgentLayer = new SubAgentDiscoveryContextLayer();
            var subAgentRegistry = new SubAgentDefinitionRegistry();
            var loader = new FileSubAgentDefinitionLoader(paths, NullLogger<FileSubAgentDefinitionLoader>.Instance);
            var writer = new McpShadowCatalogWriter(paths, registry, NullLogger<McpShadowCatalogWriter>.Instance);

            var updater = new ToolIndexUpdater(
                paths,
                writer,
                registry,
                memoryLayer,
                subAgentLayer,
                subAgentRegistry,
                loader,
                subAgentSpawner: null!,
                new SubAgentConfig(),
                NullLogger<ToolIndexUpdater>.Instance);

            await updater.StartAsync(TestContext.Current.CancellationToken);

            var discovery = subAgentLayer.GetContextLayer(TrustAudience.Personal);
            Assert.False(string.IsNullOrWhiteSpace(discovery));
            Assert.Contains("available-subagents", discovery, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(paths.AgentsDirectory, discovery, StringComparison.Ordinal);

            foreach (var tool in SubAgentToolPolicy.GetAllowedUserFacingTools())
                Assert.Contains(tool, discovery, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_keeps_public_tool_index_filtered_from_hidden_capabilities()
    {
        var tempDir = CreateTempDir();
        try
        {
            var paths = new NetclawPaths(tempDir);
            paths.EnsureDirectoriesExist();

            var config = new ToolConfig();
            var policy = new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Public,
                    TrustAudience.Public,
                    ShellExecutionMode.Off,
                    UsedStrictFallback: true),
                featureGates: new FeatureGates(SubAgentsEnabled: false, SchedulingEnabled: false));
            var registry = new ToolRegistry();
            registry.Register(AIFunctionFactory.Create(() => "ok", "file_read"), "file");
            registry.Register(AIFunctionFactory.Create(() => "ok", "set_reminder"), "builtin");
            registry.Register(new McpToolAdapter(
                AIFunctionFactory.Create(() => "ok", "search", "Search memory"),
                "memorizer",
                "search"));

            var memoryLayer = new MemoryIndexContextLayer();
            var subAgentLayer = new SubAgentDiscoveryContextLayer(new SubAgentConfig { Enabled = false });
            var toolIndexLayer = new ToolIndexContextLayer(registry, policy);
            var subAgentRegistry = new SubAgentDefinitionRegistry();
            var loader = new FileSubAgentDefinitionLoader(paths, NullLogger<FileSubAgentDefinitionLoader>.Instance);
            var writer = new McpShadowCatalogWriter(paths, registry, NullLogger<McpShadowCatalogWriter>.Instance);

            var updater = new ToolIndexUpdater(
                paths,
                writer,
                registry,
                memoryLayer,
                subAgentLayer,
                subAgentRegistry,
                loader,
                subAgentSpawner: null!,
                new SubAgentConfig { Enabled = false },
                NullLogger<ToolIndexUpdater>.Instance);

            await updater.StartAsync(TestContext.Current.CancellationToken);

            var publicIndex = toolIndexLayer.GetContextLayer(TrustAudience.Public);
            Assert.Contains("file: file_read", publicIndex);
            Assert.DoesNotContain("set_reminder", publicIndex);
            Assert.DoesNotContain("memorizer", publicIndex);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"netclaw-tool-index-updater-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
