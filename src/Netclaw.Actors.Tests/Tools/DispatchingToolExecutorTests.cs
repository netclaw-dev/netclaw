using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class DispatchingToolExecutorTests
{
    private readonly DispatchingToolExecutor _executor;

    public DispatchingToolExecutorTests()
    {
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(new ToolConfig());
        _executor = new DispatchingToolExecutor(registry);
    }

    [Fact]
    public async Task Routes_shell_execute()
    {
        var toolCall = new FunctionCallContent(
            "call-1", "shell_execute",
            new Dictionary<string, object?> { ["Command"] = "echo routed" });

        var result = await _executor.ExecuteAsync(toolCall);

        Assert.Contains("routed", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Routes_file_read_missing_file()
    {
        var toolCall = new FunctionCallContent(
            "call-2", "file_read",
            new Dictionary<string, object?> { ["Path"] = "/nonexistent/file.txt" });

        var result = await _executor.ExecuteAsync(toolCall);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Routes_file_write()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"netclaw-dispatch-{Guid.NewGuid():N}.txt");
        try
        {
            var toolCall = new FunctionCallContent(
                "call-3", "file_write",
                new Dictionary<string, object?>
                {
                    ["Path"] = filePath,
                    ["Content"] = "dispatch test"
                });

            var result = await _executor.ExecuteAsync(toolCall);

            Assert.Contains("Successfully wrote", result);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Unknown_tool_returns_error_string()
    {
        var toolCall = new FunctionCallContent(
            "call-4", "unknown_tool",
            new Dictionary<string, object?> { ["arg"] = "value" });

        var result = await _executor.ExecuteAsync(toolCall);

        Assert.Equal("Unknown tool: unknown_tool", result);
    }
}
