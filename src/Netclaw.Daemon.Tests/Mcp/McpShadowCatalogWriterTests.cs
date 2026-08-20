// -----------------------------------------------------------------------
// <copyright file="McpShadowCatalogWriterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpShadowCatalogWriterTests
{
    [Fact]
    public void WriteCatalogs_WritesToolIndexAndPerServerFiles()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create(() => "ok", "store", "Store memory"),
            "memorizer",
            "store"));
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create((string query) => "ok", "search", "Search memory"),
            "memorizer",
            "search"));
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create((string url) => "ok", "navigate", "Navigate page"),
            "browser_playwright",
            "navigate"));

        var writer = new McpShadowCatalogWriter(
            paths,
            registry,
            NullLogger<McpShadowCatalogWriter>.Instance);

        var layer = writer.WriteCatalogs();

        Assert.True(File.Exists(paths.ToolIndexShadowPath));
        Assert.Equal(layer, File.ReadAllText(paths.ToolIndexShadowPath));
        Assert.Contains("[MCP capability servers - discover tools with search_tools]", layer);
        Assert.Contains("[shadow catalogs on disk]", layer);
        Assert.Contains("memorizer", layer);
        Assert.Contains("browser_playwright", layer);

        var memorizerPath = Path.Combine(paths.McpShadowDirectory, "memorizer.md");
        var browserPath = Path.Combine(paths.McpShadowDirectory, "browser_playwright.md");

        Assert.True(File.Exists(memorizerPath));
        Assert.True(File.Exists(browserPath));

        var memorizerCatalog = File.ReadAllText(memorizerPath);
        Assert.Contains("Server: memorizer", memorizerCatalog);
        Assert.Contains("Tool Count: 2", memorizerCatalog);
        Assert.Contains("memorizer/store", memorizerCatalog);
        Assert.Contains("memorizer/search", memorizerCatalog);
        Assert.Contains("params: query", memorizerCatalog);

        var browserCatalog = File.ReadAllText(browserPath);
        Assert.Contains("Server: browser_playwright", browserCatalog);
        Assert.Contains("Tool Count: 1", browserCatalog);
        Assert.Contains("browser_playwright/navigate", browserCatalog);
        Assert.Contains("params: url", browserCatalog);
    }

    [Fact]
    public void WriteCatalogs_RemovesStaleServerFiles()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        var stalePath = Path.Combine(paths.McpShadowDirectory, "stale.md");
        File.WriteAllText(stalePath, "stale");

        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create(() => "ok", "search", "Search memory"),
            "memorizer",
            "search"));

        var writer = new McpShadowCatalogWriter(
            paths,
            registry,
            NullLogger<McpShadowCatalogWriter>.Instance);

        writer.WriteCatalogs();

        Assert.False(File.Exists(stalePath));
        Assert.True(File.Exists(Path.Combine(paths.McpShadowDirectory, "memorizer.md")));
    }

    [Fact]
    public void Operator_catalog_stays_complete_while_model_index_hides_denied_names()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create(() => "ok", "search_memories", "Search memory"),
            "memorizer",
            "search_memories"));
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create(() => "ok", "delete_memory", "Delete memory"),
            "memorizer",
            "delete_memory"));

        new McpShadowCatalogWriter(
                paths,
                registry,
                NullLogger<McpShadowCatalogWriter>.Instance)
            .WriteCatalogs();

        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Team.AllowedMcpServers.Add("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories"]
        };
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));

        var operatorCatalog = File.ReadAllText(Path.Combine(paths.McpShadowDirectory, "memorizer.md"));
        var modelIndex = new ToolIndexContextLayer(registry, policy).GetContextLayer(TrustAudience.Team);

        Assert.Contains("memorizer/search_memories", operatorCatalog);
        Assert.Contains("memorizer/delete_memory", operatorCatalog);
        Assert.Contains("memorizer (1 tools)", modelIndex);
        Assert.DoesNotContain("delete_memory", modelIndex);
    }
}
