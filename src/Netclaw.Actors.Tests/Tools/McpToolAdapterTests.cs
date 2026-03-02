using System.Text.Json;
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
    public void ToAITool_ReturnsSanitizedWrapper()
    {
        var fakeTool = AIFunctionFactory.Create(() => "result", "store");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "store");

        var aiTool = adapter.ToAITool();
        // Should return a sanitized wrapper, not the raw tool
        Assert.IsAssignableFrom<AIFunction>(aiTool);
        var func = (AIFunction)aiTool;
        Assert.Equal("memorizer/store", func.Name);
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

    [Fact]
    public async Task ExecuteAsync_CoercesStringArguments()
    {
        // Simulate Ollama sending a number as a string
        string EchoLimit(int limit) => $"limit={limit}";
        var fakeTool = AIFunctionFactory.Create(EchoLimit, "search");
        var adapter = new McpToolAdapter(fakeTool, "server", "search");

        var args = new Dictionary<string, object?> { ["limit"] = "10" };
        var result = await adapter.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal("limit=10", result);
    }

    [Fact]
    public async Task ExecuteAsync_NormalizesArgumentKeys_CaseInsensitive()
    {
        string EchoUrl(string url) => url;
        var fakeTool = AIFunctionFactory.Create(EchoUrl, "navigate_page");
        var adapter = new McpToolAdapter(fakeTool, "browser", "navigate_page");

        var args = new Dictionary<string, object?> { ["Url"] = "https://example.com" };
        var result = await adapter.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal("https://example.com", result);
    }
}

public class McpSchemaSanitizerTests
{
    [Fact]
    public void SanitizeSchema_SimplifiesNullableTypeArray()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "query": { "type": "string" },
                    "limit": { "type": ["integer", "null"], "default": 10 }
                }
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);
        var limitProp = sanitized.GetProperty("properties").GetProperty("limit");

        // Should be simplified to just "integer"
        Assert.Equal(JsonValueKind.String, limitProp.GetProperty("type").ValueKind);
        Assert.Equal("integer", limitProp.GetProperty("type").GetString());
    }

    [Fact]
    public void SanitizeSchema_PreservesNonNullableTypes()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string" }
                }
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);
        var nameProp = sanitized.GetProperty("properties").GetProperty("name");

        Assert.Equal("string", nameProp.GetProperty("type").GetString());
    }

    [Fact]
    public void CoerceArguments_ConvertsStringNumbers()
    {
        var args = new Dictionary<string, object?>
        {
            ["count"] = "42",
            ["ratio"] = "3.14",
            ["name"] = "hello",
            ["flag"] = "true"
        };

        var coerced = McpSchemaSanitizer.CoerceArguments(args)!;

        Assert.Equal(42L, coerced["count"]);
        Assert.Equal(3.14, coerced["ratio"]);
        Assert.Equal("hello", coerced["name"]);
        Assert.Equal(true, coerced["flag"]);
    }

    [Fact]
    public void CoerceArguments_ReturnsNullForNull()
    {
        Assert.Null(McpSchemaSanitizer.CoerceArguments(null));
    }

    [Fact]
    public void NormalizeArgumentKeys_UsesSchemaPropertyCasing()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "url": { "type": "string" },
                    "timeout": { "type": "integer" }
                }
            }
            """).RootElement;

        var args = new Dictionary<string, object?>
        {
            ["Url"] = "https://example.com",
            ["Timeout"] = "1000"
        };

        var normalized = McpSchemaSanitizer.NormalizeArgumentKeys(args, schema)!;

        Assert.True(normalized.ContainsKey("url"));
        Assert.True(normalized.ContainsKey("timeout"));
        Assert.False(normalized.ContainsKey("Url"));
        Assert.False(normalized.ContainsKey("Timeout"));
    }
}
