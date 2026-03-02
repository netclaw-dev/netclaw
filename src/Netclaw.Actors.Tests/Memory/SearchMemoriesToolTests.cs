using Microsoft.Extensions.AI;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class SearchMemoriesToolTests
{
    [Fact]
    public async Task Returns_unavailable_message_when_memorizer_not_connected()
    {
        var registry = new ToolRegistry();
        // No MCP tools registered — Memorizer is not connected
        var tool = new SearchMemoriesTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "test query" },
            CancellationToken.None);

        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not connected", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var registry = new ToolRegistry();
        var tool = new SearchMemoriesTool(registry);

        Assert.Equal("builtin", tool.GrantCategory);
    }

    [Fact]
    public async Task Delegates_to_memorizer_mcp_tool_when_available()
    {
        var registry = new ToolRegistry();

        // Register a fake MCP tool under the expected name
        var fakeResult = "Found 1 memory: test memory content";
        var fakeTool = AIFunctionFactory.Create((string query) => fakeResult,
            name: "search_memories",
            description: "Search memories");
        registry.Register(fakeTool, "mcp:memorizer");

        // Re-register with the expected namespaced name
        var registry2 = new ToolRegistry();
        var fakeMemorizerTool = new FakeNetclawTool("memorizer/search_memories", fakeResult);
        registry2.Register(fakeMemorizerTool);

        var tool = new SearchMemoriesTool(registry2);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "test" },
            CancellationToken.None);

        Assert.Contains("test memory content", result);
    }

    [Fact]
    public void MemoryIndexContextLayer_connected_shows_behavioral_triggers()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(connected: true);

        var content = layer.GetContextLayer();

        // Behavioral triggers — retrieve and save rules
        Assert.Contains("RETRIEVE:", content);
        Assert.Contains("SAVE:", content);
        Assert.Contains("search_memories", content);
        Assert.Contains("memorizer/store", content);

        // Organization guidance still present
        Assert.Contains("workspaces", content);
        Assert.Contains("projects", content);
        Assert.Contains("memorizer-usage", content);
        Assert.DoesNotContain("NOT AVAILABLE", content);
    }

    [Fact]
    public void MemoryIndexContextLayer_disconnected_shows_unavailable_with_fallback()
    {
        var layer = new MemoryIndexContextLayer();
        layer.Update(connected: false);

        var content = layer.GetContextLayer();

        Assert.Contains("NOT AVAILABLE", content);
        // Fallback guidance — save to identity/skill files instead
        Assert.Contains("SOUL.md", content);
        Assert.Contains("identity-management", content);
        Assert.Contains("skill", content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Minimal INetclawTool fake for testing SearchMemoriesTool delegation.
    /// </summary>
    private sealed class FakeNetclawTool : Netclaw.Tools.INetclawTool
    {
        private readonly string _result;

        public FakeNetclawTool(string name, string result)
        {
            Name = name;
            _result = result;
        }

        public string Name { get; }
        public string Description => "Fake tool";
        public string GrantCategory => "mcp:memorizer";
        public System.Text.Json.JsonElement ParameterSchema => default;
        public AITool ToAITool() => AIFunctionFactory.Create(() => _result, name: Name, description: Description);
        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => Task.FromResult(_result);
    }
}
