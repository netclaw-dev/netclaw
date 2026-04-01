using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class McpToolAdapterTests
{
    [Fact]
    public void Construction_produces_correct_shaped_adapter()
    {
        var fakeTool = AIFunctionFactory.Create(() => "result", "store");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "store");

        Assert.Equal("memorizer/store", adapter.Name);
        Assert.Equal("store", adapter.BareToolName);
        Assert.Equal("mcp:memorizer", adapter.GrantCategory);
        Assert.Equal("memorizer", adapter.ServerName);

        // Explicit grant override exercises the branch
        var withOverride = new McpToolAdapter(fakeTool, "memorizer", "store", "custom:grant");
        Assert.Equal("custom:grant", withOverride.GrantCategory);
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

    [Fact]
    public async Task ExecuteAsync_WithContext_UsesInvokerWhenConfigured()
    {
        string ThrowingFunc() => throw new InvalidOperationException("should not run");
        var fakeTool = AIFunctionFactory.Create((Func<string>)ThrowingFunc, "navigate_page");
        var invoker = new RecordingMcpToolInvoker("scoped-result");
        var adapter = new McpToolAdapter(fakeTool, "browser_playwright", "navigate_page", invoker: invoker);

        var context = new ToolExecutionContext("chan/thread", null);
        var args = new Dictionary<string, object?> { ["Url"] = "https://example.com" };

        var result = await adapter.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Equal("scoped-result", result);
        Assert.Equal("browser_playwright", invoker.ServerName);
        Assert.Equal("navigate_page", invoker.ToolName);
        Assert.Equal("chan/thread", invoker.SessionId);
        Assert.Contains(invoker.Arguments!, kvp => Equals(kvp.Value, "https://example.com"));
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_InvokerFailure_ReturnsError()
    {
        var fakeTool = AIFunctionFactory.Create(() => "unused", "navigate_page");
        var invoker = new RecordingMcpToolInvoker("ignored")
        {
            Failure = new InvalidOperationException("session transport failed")
        };
        var adapter = new McpToolAdapter(fakeTool, "browser_playwright", "navigate_page", invoker: invoker);

        var context = new ToolExecutionContext("chan/thread", null);
        var result = await adapter.ExecuteAsync(new Dictionary<string, object?>(), context, CancellationToken.None);

        Assert.StartsWith("Error:", result);
        Assert.Contains("session transport failed", result);
    }

    [Fact]
    public void ClampDescription_ExactlyAtLimit_PreservedAsIs()
    {
        var description = new string('a', 2048);
        var result = McpToolAdapter.ClampDescription(description, 2048);
        Assert.Equal(description, result);
    }

    [Fact]
    public void ClampDescription_ExceedsLimit_Truncated()
    {
        var description = new string('a', 5000);
        var result = McpToolAdapter.ClampDescription(description, 2048);
        Assert.Equal(2048 + " [truncated]".Length, result.Length);
        Assert.EndsWith(" [truncated]", result);
        Assert.StartsWith(new string('a', 2048), result);
    }

    [Fact]
    public void ClampDescription_DisabledWithZero_PreservesFullDescription()
    {
        var description = new string('a', 10000);
        var result = McpToolAdapter.ClampDescription(description, 0);
        Assert.Equal(description, result);
    }

    [Fact]
    public void Constructor_WithMaxDescriptionChars_TruncatesDescription()
    {
        var longDesc = new string('x', 5000);
        string FakeFunc() => "result";
        var fakeTool = AIFunctionFactory.Create(FakeFunc, "verbose_tool", longDesc);
        var adapter = new McpToolAdapter(fakeTool, "notion", "verbose_tool", maxDescriptionChars: 100);

        Assert.Equal(100 + " [truncated]".Length, adapter.Description.Length);
        Assert.EndsWith(" [truncated]", adapter.Description);

        // SanitizedAIFunction should also have the truncated description
        var aiFunc = (AIFunction)adapter.ToAITool();
        Assert.Equal(adapter.Description, aiFunc.Description);
    }

}

internal sealed class RecordingMcpToolInvoker(string result) : IMcpToolInvoker
{
    public string? ServerName { get; private set; }
    public string? ToolName { get; private set; }
    public IDictionary<string, object?>? Arguments { get; private set; }
    public string? SessionId { get; private set; }
    public Exception? Failure { get; init; }

    public Task<string> InvokeAsync(
        string serverName,
        string toolName,
        IDictionary<string, object?>? arguments,
        ToolExecutionContext? context,
        CancellationToken ct = default)
    {
        if (Failure is not null)
            throw Failure;

        ServerName = serverName;
        ToolName = toolName;
        Arguments = arguments;
        SessionId = context?.SessionId;
        return Task.FromResult(result);
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

    [Fact]
    public void SanitizeSchema_Strips_DollarSchema()
    {
        var schema = JsonDocument.Parse("""
            {
                "$schema": "http://json-schema.org/draft-07/schema#",
                "type": "object",
                "properties": {
                    "query": { "type": "string" }
                }
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);

        Assert.False(sanitized.TryGetProperty("$schema", out _));
        Assert.Equal("object", sanitized.GetProperty("type").GetString());
        Assert.True(sanitized.TryGetProperty("properties", out _));
    }

    [Fact]
    public void SanitizeSchema_Strips_DollarSchema_InNestedObjects()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "filters": {
                        "$schema": "http://json-schema.org/draft-07/schema#",
                        "type": "object",
                        "properties": {
                            "name": { "type": "string" }
                        }
                    }
                }
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);
        var filters = sanitized.GetProperty("properties").GetProperty("filters");

        Assert.False(filters.TryGetProperty("$schema", out _));
        Assert.Equal("object", filters.GetProperty("type").GetString());
    }

    [Fact]
    public void SanitizeSchema_NormalizesEmptyAdditionalProperties()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string" }
                },
                "additionalProperties": {}
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);

        var ap = sanitized.GetProperty("additionalProperties");
        Assert.Equal(JsonValueKind.True, ap.ValueKind);
    }

    [Fact]
    public void SanitizeSchema_PreservesBooleanAdditionalProperties()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string" }
                },
                "additionalProperties": false
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);

        var ap = sanitized.GetProperty("additionalProperties");
        Assert.Equal(JsonValueKind.False, ap.ValueKind);
    }

    [Fact]
    public void SanitizeSchema_PreservesNonEmptyAdditionalProperties()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string" }
                },
                "additionalProperties": { "type": "string" }
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);

        var ap = sanitized.GetProperty("additionalProperties");
        Assert.Equal(JsonValueKind.Object, ap.ValueKind);
        Assert.Equal("string", ap.GetProperty("type").GetString());
    }

    [Fact]
    public void SanitizeSchema_PreservesActualNullValues()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string" }
                },
                "default": null
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);

        Assert.True(sanitized.TryGetProperty("default", out var defaultProp));
        Assert.Equal(JsonValueKind.Null, defaultProp.ValueKind);
    }

    [Fact]
    public void SanitizeSchema_HandlesNotionSearchSchema()
    {
        // Real-world Notion search schema that was causing 502 errors
        var schema = JsonDocument.Parse("""
            {
                "$schema": "http://json-schema.org/draft-07/schema#",
                "type": "object",
                "properties": {
                    "query": {
                        "type": "string",
                        "minLength": 1,
                        "description": "Search query"
                    },
                    "filters": {
                        "type": "object",
                        "properties": {
                            "created_date_range": {
                                "type": "object",
                                "properties": {
                                    "start_date": { "type": "string" }
                                },
                                "additionalProperties": {}
                            }
                        },
                        "additionalProperties": {}
                    }
                },
                "required": ["query", "filters"]
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);

        // $schema stripped at top level
        Assert.False(sanitized.TryGetProperty("$schema", out _));

        // additionalProperties: {} normalized to true in nested objects
        var filters = sanitized.GetProperty("properties").GetProperty("filters");
        Assert.Equal(JsonValueKind.True, filters.GetProperty("additionalProperties").ValueKind);

        var dateRange = filters.GetProperty("properties").GetProperty("created_date_range");
        Assert.Equal(JsonValueKind.True, dateRange.GetProperty("additionalProperties").ValueKind);

        // Core schema structure preserved
        Assert.Equal("object", sanitized.GetProperty("type").GetString());
        Assert.Equal(2, sanitized.GetProperty("required").GetArrayLength());
    }
}
