using Netclaw.Actors.Memory;
using Netclaw.Actors.Tests.SubAgents;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class MemorizerFindMemoriesToolTests
{
    [Fact]
    public async Task Search_delegates_to_memorizer_search_tool()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeNetclawTool("memorizer/search_memories", "found 3 results"));

        var tool = new MemorizerFindMemoriesTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "akka clustering"
            },
            CancellationToken.None);

        Assert.Equal("found 3 results", result);
    }

    [Fact]
    public async Task Returns_unavailable_when_no_memorizer_tools()
    {
        var registry = new ToolRegistry();

        var tool = new MemorizerFindMemoriesTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "something"
            },
            CancellationToken.None);

        Assert.Contains("unavailable", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var registry = new ToolRegistry();
        var tool = new MemorizerFindMemoriesTool(registry);

        Assert.Equal("builtin", tool.GrantCategory);
    }

    [Fact]
    public async Task Passes_tags_as_filter_tags()
    {
        var fakeTool = new FakeNetclawTool("memorizer/search_memories", "filtered results");
        var registry = new ToolRegistry();
        registry.Register(fakeTool);

        var tool = new MemorizerFindMemoriesTool(registry);

        await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "test",
                ["Tags"] = "reference, how-to"
            },
            CancellationToken.None);

        Assert.True(fakeTool.WasCalled);
        Assert.NotNull(fakeTool.LastArguments);
        Assert.True(fakeTool.LastArguments!.ContainsKey("filterTags"));
    }
}
