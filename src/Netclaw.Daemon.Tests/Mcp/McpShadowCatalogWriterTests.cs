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
            AIFunctionFactory.Create((string url) => "ok", "navigate", "Navigate page"),
            "browser_playwright",
            "navigate"));

        var writer = new McpShadowCatalogWriter(
            paths,
            registry,
            NullLogger<McpShadowCatalogWriter>.Instance);

        var layer = writer.WriteCatalogs();

        Assert.True(File.Exists(paths.ToolIndexShadowPath));
        Assert.Contains("[MCP capability servers - discover tools with search_tools]", layer);
        Assert.Contains("[shadow catalogs on disk]", layer);

        var memorizerPath = Path.Combine(paths.McpShadowDirectory, "memorizer.md");
        var browserPath = Path.Combine(paths.McpShadowDirectory, "browser_playwright.md");

        Assert.True(File.Exists(memorizerPath));
        Assert.True(File.Exists(browserPath));

        var memorizerCatalog = File.ReadAllText(memorizerPath);
        Assert.Contains("Server: memorizer", memorizerCatalog);
        Assert.Contains("memorizer/store", memorizerCatalog);
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
}
