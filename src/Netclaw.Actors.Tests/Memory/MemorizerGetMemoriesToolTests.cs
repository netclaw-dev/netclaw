using Netclaw.Actors.Memory;
using Netclaw.Actors.Tests.SubAgents;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class MemorizerGetMemoriesToolTests
{
    [Fact]
    public async Task Get_delegates_to_memorizer_get_many()
    {
        var fakeTool = new FakeNetclawTool("memorizer/get_many", "full content of memory 1\nfull content of memory 2");
        var registry = new ToolRegistry();
        registry.Register(fakeTool);

        var tool = new MemorizerGetMemoriesTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Ids"] = "id-1, id-2"
            },
            CancellationToken.None);

        Assert.True(fakeTool.WasCalled);
        Assert.Contains("full content of memory 1", result);
    }

    [Fact]
    public async Task Returns_unavailable_when_no_memorizer_tools()
    {
        var registry = new ToolRegistry();

        var tool = new MemorizerGetMemoriesTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Ids"] = "some-id"
            },
            CancellationToken.None);

        Assert.Contains("unavailable", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var registry = new ToolRegistry();
        var tool = new MemorizerGetMemoriesTool(registry);

        Assert.Equal("builtin", tool.GrantCategory);
    }

    [Fact]
    public async Task Splits_comma_separated_ids_into_array()
    {
        var fakeTool = new FakeNetclawTool("memorizer/get_many", "results");
        var registry = new ToolRegistry();
        registry.Register(fakeTool);

        var tool = new MemorizerGetMemoriesTool(registry);

        await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Ids"] = "id-a, id-b, id-c" },
            CancellationToken.None);

        Assert.NotNull(fakeTool.LastArguments);
        var ids = fakeTool.LastArguments!["ids"] as string[];
        Assert.NotNull(ids);
        Assert.Equal(3, ids!.Length);
        Assert.Equal("id-a", ids[0]);
        Assert.Equal("id-b", ids[1]);
        Assert.Equal("id-c", ids[2]);
    }
}
