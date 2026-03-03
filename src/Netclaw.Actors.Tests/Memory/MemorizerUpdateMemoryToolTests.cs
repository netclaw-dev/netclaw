using Netclaw.Actors.Memory;
using Netclaw.Actors.Tests.SubAgents;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class MemorizerUpdateMemoryToolTests
{
    [Fact]
    public async Task Delete_delegates_to_memorizer_archive()
    {
        var fakeTool = new FakeNetclawTool("memorizer/archive_memory", "archived");
        var registry = new ToolRegistry();
        registry.Register(fakeTool);

        var tool = new MemorizerUpdateMemoryTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = "abc-123",
                ["Delete"] = "true"
            },
            CancellationToken.None);

        Assert.True(fakeTool.WasCalled);
        Assert.Contains("archived", result);
        Assert.Equal("abc-123", fakeTool.LastArguments!["id"]);
    }

    [Fact]
    public async Task Edit_delegates_to_memorizer_edit()
    {
        var fakeTool = new FakeNetclawTool("memorizer/edit", "edited successfully");
        var registry = new ToolRegistry();
        registry.Register(fakeTool);

        var tool = new MemorizerUpdateMemoryTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = "abc-123",
                ["OldText"] = "old content",
                ["NewText"] = "new content"
            },
            CancellationToken.None);

        Assert.True(fakeTool.WasCalled);
        Assert.Contains("updated", result);
        Assert.Equal("abc-123", fakeTool.LastArguments!["id"]);
        Assert.Equal("old content", fakeTool.LastArguments["old_text"]);
        Assert.Equal("new content", fakeTool.LastArguments["new_text"]);
    }

    [Fact]
    public async Task Returns_unavailable_when_no_memorizer_tools()
    {
        var registry = new ToolRegistry();

        var tool = new MemorizerUpdateMemoryTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = "abc-123",
                ["Delete"] = "true"
            },
            CancellationToken.None);

        Assert.Contains("unavailable", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Returns_error_when_missing_params()
    {
        var registry = new ToolRegistry();

        var tool = new MemorizerUpdateMemoryTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = "abc-123"
            },
            CancellationToken.None);

        Assert.Contains("Error", result);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var registry = new ToolRegistry();
        var tool = new MemorizerUpdateMemoryTool(registry);

        Assert.Equal("builtin", tool.GrantCategory);
    }
}
