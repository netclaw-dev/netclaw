using Microsoft.Extensions.AI;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public class MemorizerMemoryExtractorTests
{
    [Fact]
    public async Task PersistAsync_calls_memorizer_store_tool()
    {
        var registry = new ToolRegistry();
        var fakeTool = new FakeNetclawTool("memorizer/store", "stored");
        registry.Register(fakeTool);

        var extractor = new MemorizerMemoryExtractor(registry);

        await extractor.PersistAsync("chan/ts1", "Important finding: X depends on Y.");

        Assert.True(fakeTool.WasCalled);
        Assert.NotNull(fakeTool.LastArguments);
        Assert.Equal("Session extraction — chan/ts1", fakeTool.LastArguments!["title"]);
        Assert.Equal("Important finding: X depends on Y.", fakeTool.LastArguments["text"]);
    }

    [Fact]
    public async Task PersistAsync_is_noop_when_memorizer_not_connected()
    {
        var registry = new ToolRegistry();
        // No tools registered — Memorizer not connected
        var extractor = new MemorizerMemoryExtractor(registry);

        // Should not throw
        await extractor.PersistAsync("chan/ts1", "Some content.");
    }

    [Fact]
    public async Task PersistAsync_skips_empty_content()
    {
        var registry = new ToolRegistry();
        var fakeTool = new FakeNetclawTool("memorizer/store", "stored");
        registry.Register(fakeTool);

        var extractor = new MemorizerMemoryExtractor(registry);

        await extractor.PersistAsync("chan/ts1", "");
        await extractor.PersistAsync("chan/ts2", "   ");

        Assert.False(fakeTool.WasCalled);
    }

}

/// <summary>
/// Reusable fake <see cref="INetclawTool"/> for tests that need tools in a <see cref="ToolRegistry"/>.
/// </summary>
internal sealed class FakeNetclawTool : INetclawTool
{
    private readonly string _result;

    public FakeNetclawTool(string name, string result, string grantCategory = "mcp:memorizer")
    {
        Name = name;
        _result = result;
        GrantCategory = grantCategory;
    }

    public string Name { get; }
    public string Description => "Fake tool";
    public string GrantCategory { get; }
    public System.Text.Json.JsonElement ParameterSchema => default;

    public bool WasCalled { get; private set; }
    public IDictionary<string, object?>? LastArguments { get; private set; }

    public AITool ToAITool() => AIFunctionFactory.Create(() => _result, name: Name, description: Description);

    public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
    {
        WasCalled = true;
        LastArguments = arguments;
        return Task.FromResult(_result);
    }
}
