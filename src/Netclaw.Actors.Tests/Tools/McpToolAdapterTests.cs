using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class McpToolAdapterTests
{
    [Fact]
    public void Name_IsNamespaced()
    {
        var fakeTool = AIFunctionFactory.Create(() => "result", "store");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "store");

        Assert.Equal("memorizer/store", adapter.Name);
    }

    [Fact]
    public void BareToolName_ReturnsUnprefixedName()
    {
        var fakeTool = AIFunctionFactory.Create(() => "result", "store");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "store");

        Assert.Equal("store", adapter.BareToolName);
    }

    [Fact]
    public void GrantCategory_DefaultsToMcpPrefix()
    {
        var fakeTool = AIFunctionFactory.Create(() => "result", "store");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "store");

        Assert.Equal("mcp:memorizer", adapter.GrantCategory);
    }

    [Fact]
    public void GrantCategory_UsesExplicitOverride()
    {
        var fakeTool = AIFunctionFactory.Create(() => "result", "store");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "store", "custom:grant");

        Assert.Equal("custom:grant", adapter.GrantCategory);
    }

    [Fact]
    public void ServerName_IsSet()
    {
        var fakeTool = AIFunctionFactory.Create(() => "result", "store");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "store");

        Assert.Equal("memorizer", adapter.ServerName);
    }

    [Fact]
    public void ToAITool_ReturnsSameInstance()
    {
        var fakeTool = AIFunctionFactory.Create(() => "result", "store");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "store");

        Assert.Same(fakeTool, adapter.ToAITool());
    }

    [Fact]
    public async Task ExecuteAsync_InvokesUnderlyingTool()
    {
        var fakeTool = AIFunctionFactory.Create(() => "hello from mcp", "greet");
        var adapter = new McpToolAdapter(fakeTool, "server", "greet");

        var result = await adapter.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal("hello from mcp", result);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesException()
    {
        string ThrowingFunc() => throw new InvalidOperationException("connection lost");
        var fakeTool = AIFunctionFactory.Create((Func<string>)ThrowingFunc, "fail_tool");
        var adapter = new McpToolAdapter(fakeTool, "server", "fail_tool");

        var result = await adapter.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Contains("connection lost", result);
        Assert.StartsWith("Error:", result);
    }
}
