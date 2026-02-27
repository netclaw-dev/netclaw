using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class SearchToolsToolTests
{
    [Fact]
    public async Task Search_MatchesByName()
    {
        var registry = CreateRegistryWithMcpTools();
        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "store" },
            CancellationToken.None);

        Assert.Contains("memorizer/store", result);
    }

    [Fact]
    public async Task Search_MatchesByDescription()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("search_memories", "Find stored memories"),
            "memorizer", "search_memories"));

        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "memories" },
            CancellationToken.None);

        Assert.Contains("memorizer/search_memories", result);
    }

    [Fact]
    public async Task Search_NoResults()
    {
        var registry = CreateRegistryWithMcpTools();
        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "nonexistent_xyz" },
            CancellationToken.None);

        Assert.Contains("No tools found", result);
    }

    [Fact]
    public async Task Search_FiltersServer()
    {
        var registry = CreateRegistryWithMcpTools();
        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "store",
                ["Server"] = "github"
            },
            CancellationToken.None);

        // "github" server has no "store" tool — only memorizer does
        Assert.Contains("No tools found", result);
    }

    [Fact]
    public async Task Search_IncludesNonMcpToolsWithoutServerFilter()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeToolInRegistry("shell_execute", "Execute shell command"), "shell");

        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "shell" },
            CancellationToken.None);

        // Without server filter, non-MCP tools are included in search results
        Assert.Contains("shell_execute", result);
    }

    [Fact]
    public void GrantCategory_IsBuiltin()
    {
        var registry = new ToolRegistry();
        var tool = new SearchToolsTool(registry);

        Assert.Equal("builtin", tool.GrantCategory);
    }

    private static ToolRegistry CreateRegistryWithMcpTools()
    {
        var registry = new ToolRegistry();

        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("store", "Store a value"), "memorizer", "store"));
        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("search", "Search stored values"), "memorizer", "search"));
        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("create_issue", "Create a GitHub issue"), "github", "create_issue"));

        return registry;
    }

    private static AIFunction CreateFakeAIFunction(string name, string description)
    {
        return AIFunctionFactory.Create(() => "result", name, description);
    }

    private static AITool CreateFakeToolInRegistry(string name, string description)
    {
        return AIFunctionFactory.Create(() => "result", name, description);
    }
}
