using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpShadowCatalogWriterTests
{
    [Fact]
    public void WriteCatalogs_WritesToolIndexAndPerServerFiles()
    {
        var tempDir = CreateTempDir();
        try
        {
            var paths = new NetclawPaths(tempDir);
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
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WriteCatalogs_RemovesStaleServerFiles()
    {
        var tempDir = CreateTempDir();
        try
        {
            var paths = new NetclawPaths(tempDir);
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
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"netclaw-shadow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
