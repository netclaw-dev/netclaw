using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class ToolRegistryTests
{
    [Fact]
    public void GetAllTools_returns_all_registered_tools()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("search"), "web_search");
        registry.Register(CreateFakeTool("fetch"), "web_fetch");
        registry.Register(CreateFakeTool("run_shell"), "shell");

        var tools = registry.GetAllTools();

        Assert.Equal(3, tools.Count);
    }

    [Fact]
    public void GetToolsForGrants_filters_by_granted_categories()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("search"), "web_search");
        registry.Register(CreateFakeTool("fetch"), "web_fetch");
        registry.Register(CreateFakeTool("run_shell"), "shell");
        registry.Register(CreateFakeTool("gh_issue"), "github");

        var granted = new HashSet<string> { "web_search", "shell" };
        var tools = registry.GetToolsForGrants(granted);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t is AIFunction f && f.Name == "search");
        Assert.Contains(tools, t => t is AIFunction f && f.Name == "run_shell");
    }

    [Fact]
    public void GetToolsForGrants_empty_grants_returns_nothing()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("search"), "web_search");

        var tools = registry.GetToolsForGrants(new HashSet<string>());

        Assert.Empty(tools);
    }

    [Fact]
    public void GetAllTools_empty_registry_returns_empty()
    {
        var registry = new ToolRegistry();
        Assert.Empty(registry.GetAllTools());
    }

    [Fact]
    public void Multiple_tools_in_same_grant_category_all_returned()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("mcp_memorizer_store"), "mcp:memorizer");
        registry.Register(CreateFakeTool("mcp_memorizer_search"), "mcp:memorizer");

        var granted = new HashSet<string> { "mcp:memorizer" };
        var tools = registry.GetToolsForGrants(granted);

        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public void GetAlwaysLoadedTools_returns_non_mcp_tools()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("shell_execute"), "shell");
        registry.Register(CreateFakeTool("search_tools"), "builtin");
        registry.Register(CreateFakeTool("file_read"), "file");
        // MCP tools should be excluded
        registry.Register(new McpToolAdapter(
            CreateFakeTool("store"), "memorizer", "store"));

        var alwaysLoaded = registry.GetAlwaysLoadedTools();

        Assert.Equal(3, alwaysLoaded.Count);
        Assert.DoesNotContain(alwaysLoaded, t => t is AIFunction f && f.Name == "store");
    }

    [Fact]
    public void SearchTools_finds_matching_tools()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("store"), "memorizer", "store"));
        registry.Register(new McpToolAdapter(
            CreateFakeTool("search"), "memorizer", "search"));

        var results = registry.SearchTools("store", null, 10);

        Assert.Single(results);
        Assert.Equal("memorizer/store", results[0].Name);
    }

    [Fact]
    public void SearchTools_filters_by_server()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("store"), "memorizer", "store"));
        registry.Register(new McpToolAdapter(
            CreateFakeTool("store"), "github", "store"));

        var results = registry.SearchTools("store", "memorizer", 10);

        Assert.Single(results);
        Assert.Equal("memorizer/store", results[0].Name);
    }

    [Fact]
    public void GenerateCompressedIndex_groups_by_category()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("store"), "memorizer", "store"));
        registry.Register(new McpToolAdapter(
            CreateFakeTool("search"), "memorizer", "search"));
        registry.Register(CreateFakeTool("shell_execute"), "shell");

        var index = registry.GenerateCompressedIndex();

        Assert.Contains("mcp:memorizer: store, search", index);
        Assert.Contains("shell: shell_execute", index);
        Assert.Contains("search_tools", index);
    }

    [Fact]
    public void GenerateCompressedIndex_empty_registry_returns_empty()
    {
        var registry = new ToolRegistry();
        var index = registry.GenerateCompressedIndex();

        Assert.Empty(index);
    }

    private static AIFunction CreateFakeTool(string name)
    {
        return AIFunctionFactory.Create(() => "result", name);
    }
}
