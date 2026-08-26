// -----------------------------------------------------------------------
// <copyright file="McpToolAdapterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
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
    public void Suppresses_output_redaction_because_mcp_servers_are_trusted()
    {
        var fakeTool = AIFunctionFactory.Create(() => "result", "store");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "store");

        // MCP servers are trusted, user-configured integrations, so their output
        // flows to the model verbatim (like Claude Code / Cursor). Redacting it
        // corrupts legitimate payloads such as presigned upload URLs.
        Assert.True(adapter.SuppressOutputRedaction);
    }

    [Fact]
    public void LlmFacingName_uses_double_underscore_separator()
    {
        // MCP tool names commonly include single underscores
        // (e.g. find_completed_tasks); double underscore between server and
        // tool keeps the boundary unambiguous when parsing the alias back.
        var fakeTool = AIFunctionFactory.Create(() => "result", "find_completed_tasks");
        var adapter = new McpToolAdapter(fakeTool, "todoist", "find_completed_tasks");

        Assert.Equal("todoist/find_completed_tasks", adapter.Name);
        Assert.Equal("todoist__find_completed_tasks", adapter.LlmFacingName.Value);
    }

    [Theory]
    [InlineData("memorizer", "store")]
    [InlineData("todoist", "find-tasks")]
    [InlineData("browser_chrome_devtools", "navigate_page")]
    [InlineData("bamboohr", "get_employee_details")]
    public void LlmFacingName_matches_Anthropic_tool_name_regex(string server, string tool)
    {
        // Anthropic's documented tool-name constraint is
        // ^[a-zA-Z0-9_-]{1,64}$ (see Define tools docs). The whole point of
        // the alias is to satisfy this — pin the contract so future name
        // formats can't silently regress.
        var fakeTool = AIFunctionFactory.Create(() => "result", tool);
        var adapter = new McpToolAdapter(fakeTool, server, tool);

        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", adapter.LlmFacingName.Value);
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", ((AIFunction)adapter.ToAITool()).Name);
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
        // The LLM-facing AIFunction exposes the Anthropic-safe alias so the
        // tool name passes ^[a-zA-Z0-9_-]{1,64}$. The canonical Name on the
        // adapter itself still uses the '/' separator for skill text and
        // registry keys.
        Assert.Equal("memorizer__store", func.Name);
        Assert.Equal("memorizer/store", adapter.Name);
    }

    [Fact]
    public async Task ExecuteAsync_InvokesUnderlyingTool()
    {
        var fakeTool = AIFunctionFactory.Create(() => "hello from mcp", "greet");
        var adapter = new McpToolAdapter(fakeTool, "server", "greet");

        var result = await adapter.ExecuteAsync(ToolInput.Empty(), TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Equal("hello from mcp", result);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesException()
    {
        string ThrowingFunc() => throw new InvalidOperationException("connection lost");
        var fakeTool = AIFunctionFactory.Create((Func<string>)ThrowingFunc, "fail_tool");
        var adapter = new McpToolAdapter(fakeTool, "server", "fail_tool");

        var result = await adapter.ExecuteAsync(ToolInput.Empty(), TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

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

        var args = ToolInput.Create("limit", "10");
        var result = await adapter.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Equal("limit=10", result);
    }

    [Fact]
    public async Task ExecuteAsync_NormalizesArgumentKeys_CaseInsensitive()
    {
        string EchoUrl(string url) => url;
        var fakeTool = AIFunctionFactory.Create(EchoUrl, "navigate_page");
        var adapter = new McpToolAdapter(fakeTool, "browser", "navigate_page");

        var args = ToolInput.Create("Url", "https://example.com");
        var result = await adapter.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Equal("https://example.com", result);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_UsesInvokerWhenConfigured()
    {
        string ThrowingFunc() => throw new InvalidOperationException("should not run");
        var fakeTool = AIFunctionFactory.Create((Func<string>)ThrowingFunc, "navigate_page");
        var invoker = new RecordingMcpToolInvoker("scoped-result");
        var adapter = new McpToolAdapter(fakeTool, "browser_playwright", "navigate_page", invoker: invoker);

        var context = TestToolExecutionContext.CreateBound("chan/thread", null, TrustAudience.Personal);
        var args = ToolInput.Create("Url", "https://example.com");

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

        var context = TestToolExecutionContext.CreateBound("chan/thread", null, TrustAudience.Personal);
        var result = await adapter.ExecuteAsync(ToolInput.Empty(), context, CancellationToken.None);

        Assert.StartsWith("Error:", result);
        Assert.Contains("session transport failed", result);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_HttpServerError_RecordsTransientFailure()
    {
        var context = await ExecuteWithFailureAsync(
            new HttpRequestException("upstream error", null, HttpStatusCode.InternalServerError));

        Assert.Equal(ToolInvocationOutcomeCategory.TransientFailure, context.Receipt?.Category);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_HttpUnauthorized_RecordsAccessDenied()
    {
        // The manager also moves the server to AuthFailed on this status. The result the
        // model reads must still name the denial, not a transient fault to retry.
        var context = await ExecuteWithFailureAsync(
            new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized));

        Assert.Equal(ToolInvocationOutcomeCategory.AccessDenied, context.Receipt?.Category);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_HttpForbidden_RecordsAccessDenied()
    {
        var context = await ExecuteWithFailureAsync(
            new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden));

        Assert.Equal(ToolInvocationOutcomeCategory.AccessDenied, context.Receipt?.Category);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_HttpNotFound_RecordsNotFound()
    {
        var context = await ExecuteWithFailureAsync(
            new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        Assert.Equal(ToolInvocationOutcomeCategory.NotFound, context.Receipt?.Category);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_McpProtocolError_RecordsTransientFailure()
    {
        var context = await ExecuteWithFailureAsync(new McpException("application MCP failure"));

        Assert.Equal(ToolInvocationOutcomeCategory.TransientFailure, context.Receipt?.Category);
    }

    /// <summary>
    /// Runs one failed MCP tool call and asserts the shared result contract: the text names
    /// the tool, so the model can tell which call failed. The caller asserts the category.
    /// </summary>
    private static async Task<ToolExecutionContext> ExecuteWithFailureAsync(Exception failure)
    {
        var fakeTool = AIFunctionFactory.Create(() => "unused", "navigate_page");
        var invoker = new RecordingMcpToolInvoker("ignored") { Failure = failure };
        var adapter = new McpToolAdapter(fakeTool, "browser_playwright", "navigate_page", invoker: invoker);
        var context = TestToolExecutionContext.CreateBound("chan/thread", null, TrustAudience.Personal);

        var result = await adapter.ExecuteAsync(ToolInput.Empty(), context, CancellationToken.None);

        Assert.StartsWith("Error: MCP tool '", result, StringComparison.Ordinal);
        Assert.Contains("browser_playwright/navigate_page", result, StringComparison.Ordinal);
        Assert.Contains(failure.Message, result, StringComparison.Ordinal);
        return context;
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fakeTool = AIFunctionFactory.Create(() => "unused", "navigate_page");
        var invoker = new RecordingMcpToolInvoker("ignored")
        {
            Failure = new OperationCanceledException(cancellation.Token)
        };
        var adapter = new McpToolAdapter(fakeTool, "browser_playwright", "navigate_page", invoker: invoker);
        var context = TestToolExecutionContext.CreateBound("chan/thread", null, TrustAudience.Personal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ExecuteAsync(ToolInput.Empty(), context, cancellation.Token));
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
        ToolInvocationContext context,
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

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void CoerceArguments_ReturnsNullForNullArguments()
    {
        Assert.Null(McpSchemaSanitizer.CoerceArguments(null, Schema("""{ "type": "object" }""")));
    }

    [Fact]
    public void CoerceArguments_CoercesStringScalars_OnlyTowardTheDeclaredType()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "count": { "type": "integer" },
                "ratio": { "type": "number" },
                "flag":  { "type": "boolean" },
                "name":  { "type": "string" }
              }
            }
            """);
        var args = ToolInput.Create("count", "42", "ratio", "3.14", "name", "hello", "flag", "true");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        Assert.Equal(42L, coerced["count"]);
        Assert.Equal(3.14, coerced["ratio"]);
        Assert.Equal(true, coerced["flag"]);
        // A string-declared parameter is never re-typed, even though "hello"
        // is a valid string either way.
        Assert.Equal("hello", coerced["name"]);
    }

    [Fact]
    public void CoerceArguments_StringDeclaredParameter_PreservesValuesThatResembleOtherTypes()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "projectId": { "type": "string" },
                "answer":    { "type": "string" }
              }
            }
            """);
        // "00713" must not become 713; "true" must not become a boolean.
        var args = ToolInput.Create("projectId", "00713", "answer", "true");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        Assert.Equal("00713", coerced["projectId"]);
        Assert.Equal("true", coerced["answer"]);
    }

    [Fact]
    public void CoerceArguments_IntegerDeclaredParameter_DoesNotAcceptFractionalStrings()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "count": { "type": "integer" }
              }
            }
            """);
        var args = ToolInput.Create("count", "3.14");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        Assert.Equal("3.14", coerced["count"]);
    }

    [Fact]
    public void CoerceArguments_StringifiedArrayOfObjects_IsReconstructed()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);
        var args = ToolInput.Create("tasks", "[{\"content\":\"A\"},{\"content\":\"B\"}]");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        var element = Assert.IsType<JsonElement>(coerced["tasks"]);
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        Assert.Equal(2, element.GetArrayLength());
        Assert.Equal("A", element[0].GetProperty("content").GetString());
    }

    [Fact]
    public void CoerceArguments_StringifiedArray_ArrivingAsJsonElementString_IsReconstructed()
    {
        // The shape FunctionCallContent.Arguments actually delivers: a
        // double-encoded value is a JsonElement of ValueKind.String, not a
        // System.String.
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);
        var jsonElementString = JsonSerializer.SerializeToElement("[{\"content\":\"A\"}]");
        Assert.Equal(JsonValueKind.String, jsonElementString.ValueKind);
        var args = ToolInput.Create("tasks", jsonElementString);

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        var element = Assert.IsType<JsonElement>(coerced["tasks"]);
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        Assert.Equal("A", element[0].GetProperty("content").GetString());
    }

    [Fact]
    public void CoerceArguments_StringifiedObject_IsReconstructed()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": { "payload": { "type": "object" } }
            }
            """);
        var args = ToolInput.Create("payload", "{\"foo\":1}");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        var element = Assert.IsType<JsonElement>(coerced["payload"]);
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal(1, element.GetProperty("foo").GetInt32());
    }

    [Fact]
    public void CoerceArguments_NullableUnionArraySchema_IsRecognized()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tags": { "type": ["array", "null"], "items": { "type": "string" } }
              }
            }
            """);
        var args = ToolInput.Create("tags", "[\"a\",\"b\"]");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        var element = Assert.IsType<JsonElement>(coerced["tags"]);
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        Assert.Equal(2, element.GetArrayLength());
    }

    [Fact]
    public void CoerceArguments_AlreadyStructuredArray_IsPassedThroughUnchanged()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);
        var structured = JsonDocument.Parse("[{\"content\":\"A\"}]").RootElement;
        var args = ToolInput.Create("tasks", structured);

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        Assert.Same(args["tasks"], coerced["tasks"]);
    }

    [Fact]
    public void CoerceArguments_StringWhoseParsedKindDiffersFromSchema_IsLeftUnchanged()
    {
        // Schema declares array; the string parses as a JSON object — refuse
        // to coerce across kinds.
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);
        var args = ToolInput.Create("tasks", "{\"oops\":true}");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        Assert.Equal("{\"oops\":true}", coerced["tasks"]);
    }

    [Fact]
    public void CoerceArguments_UnparseableStringForContainerSchema_IsLeftUnchanged()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);
        var args = ToolInput.Create("tasks", "not json at all");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        Assert.Equal("not json at all", coerced["tasks"]);
    }

    [Fact]
    public void CoerceArguments_UndeclaredTypeParameters_ArePassedThrough()
    {
        // `note` has an empty `{}` schema; `payload` is typed only via anyOf.
        // Neither declares a `type`, so neither value is coerced.
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "note": {},
                "payload": { "anyOf": [ { "type": "string" }, { "type": "object" } ] }
              }
            }
            """);
        var args = ToolInput.Create("note", "42", "payload", "{\"a\":1}");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        Assert.Equal("42", coerced["note"]);
        Assert.Equal("{\"a\":1}", coerced["payload"]);
    }

    [Fact]
    public void CoerceArguments_WithNoUsableSchema_PassesEveryValueThrough()
    {
        // Defensive guard: a default (Undefined) JsonElement schema.
        var args = ToolInput.Create("count", "42", "tasks", "[{\"x\":1}]");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, default)!;

        Assert.Equal("42", coerced["count"]);
        Assert.Equal("[{\"x\":1}]", coerced["tasks"]);
    }

    [Fact]
    public void CoerceArguments_DoesNotMutateTheInputDictionary()
    {
        // Coercion runs after authorization; it must never alter the argument
        // values an authorization or approval decision already evaluated.
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);
        var args = ToolInput.Create("tasks", "[{\"content\":\"A\"}]");

        var coerced = McpSchemaSanitizer.CoerceArguments(args, schema)!;

        Assert.NotSame(args, coerced);
        Assert.Equal("[{\"content\":\"A\"}]", args["tasks"]); // input untouched
        Assert.IsType<JsonElement>(coerced["tasks"]);          // output coerced
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

        var args = ToolInput.Create("Url", "https://example.com", "Timeout", "1000");

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

    [Theory]
    [InlineData("pattern", "\"^\\\\d{4}-\\\\d{2}-\\\\d{2}$\"")]
    [InlineData("patternProperties", "{ \"^x-\": { \"type\": \"string\" } }")]
    [InlineData("propertyNames", "{ \"pattern\": \"^[a-z]+$\" }")]
    [InlineData("not", "{ \"type\": \"null\" }")]
    [InlineData("if", "{ \"properties\": { \"kind\": { \"const\": \"a\" } } }")]
    [InlineData("then", "{ \"required\": [\"x\"] }")]
    [InlineData("else", "{ \"required\": [\"y\"] }")]
    [InlineData("multipleOf", "10")]
    [InlineData("contentEncoding", "\"base64\"")]
    [InlineData("contentMediaType", "\"image/png\"")]
    [InlineData("contentSchema", "{ \"type\": \"string\" }")]
    public void SanitizeSchema_Strips_LlamaCppIncompatibleKeyword(string keyword, string valueJson)
    {
        // These keywords either crash llama.cpp's json_schema_to_grammar
        // converter (pattern/patternProperties/propertyNames regex family),
        // are unsupported and fatal (not, if/then/else, multipleOf), or are
        // silently dropped (contentEncoding/MediaType/Schema). We strip them
        // uniformly so a pathological MCP tool schema can't take down the
        // backend.
        var schema = JsonDocument.Parse($$"""
            {
                "type": "object",
                "properties": {
                    "field": {
                        "type": "string",
                        "description": "Kept verbatim",
                        "{{keyword}}": {{valueJson}}
                    }
                }
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);
        var field = sanitized.GetProperty("properties").GetProperty("field");

        Assert.False(field.TryGetProperty(keyword, out _), $"{keyword} should be stripped");
        // Adjacent metadata is preserved so the LLM still understands the field
        Assert.Equal("string", field.GetProperty("type").GetString());
        Assert.Equal("Kept verbatim", field.GetProperty("description").GetString());
    }

    [Fact]
    public void SanitizeSchema_StripsPattern_FromNestedArrayItems()
    {
        // Regression coverage: recursion through SanitizeSchemaArray and the
        // items handler must still reach strip rules so a pathological pattern
        // inside a nested array item can't slip through.
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "dates": {
                        "type": "array",
                        "items": {
                            "type": "string",
                            "pattern": "^\\d{4}-\\d{2}-\\d{2}$"
                        }
                    }
                }
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);
        var items = sanitized.GetProperty("properties")
            .GetProperty("dates")
            .GetProperty("items");

        Assert.False(items.TryGetProperty("pattern", out _));
        Assert.Equal("string", items.GetProperty("type").GetString());
    }

    [Fact]
    public void SanitizeSchema_Preserves_LlamaCppSupportedKeywords()
    {
        // Guard against future over-stripping. These keywords are handled by
        // llama.cpp's converter and must survive sanitization — real MCP tool
        // schemas (including Notion's) rely on them.
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "date": {
                        "type": "string",
                        "format": "date",
                        "minLength": 10,
                        "maxLength": 10
                    },
                    "count": {
                        "type": "integer",
                        "minimum": 0,
                        "maximum": 1000
                    },
                    "mode": { "enum": ["a", "b", "c"] },
                    "ids": {
                        "type": "array",
                        "items": { "type": "string" },
                        "minItems": 1,
                        "maxItems": 100
                    },
                    "union": {
                        "oneOf": [
                            { "type": "string" },
                            { "type": "integer" }
                        ]
                    }
                },
                "required": ["date"]
            }
            """).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);
        var props = sanitized.GetProperty("properties");

        Assert.Equal("date", props.GetProperty("date").GetProperty("format").GetString());
        Assert.Equal(10, props.GetProperty("date").GetProperty("minLength").GetInt32());
        Assert.Equal(1000, props.GetProperty("count").GetProperty("maximum").GetInt32());
        Assert.Equal(3, props.GetProperty("mode").GetProperty("enum").GetArrayLength());
        Assert.Equal(100, props.GetProperty("ids").GetProperty("maxItems").GetInt32());
        Assert.Equal(2, props.GetProperty("union").GetProperty("oneOf").GetArrayLength());
    }

    [Fact]
    public void SanitizeSchema_RealNotionSearchFixture_StripsLeapYearPattern()
    {
        // Regression: notion-search's leap-year ISO date `pattern` must be
        // stripped so llama.cpp's grammar compiler can't SEGV on it.
        var raw = TestFixtures.Load("notion-search-input.raw.json");
        var schema = JsonDocument.Parse(raw).RootElement;

        var sanitized = McpSchemaSanitizer.SanitizeSchema(schema);

        var dateRange = sanitized
            .GetProperty("properties")
            .GetProperty("filters")
            .GetProperty("properties")
            .GetProperty("created_date_range")
            .GetProperty("properties");

        var startDate = dateRange.GetProperty("start_date");
        var endDate = dateRange.GetProperty("end_date");

        Assert.False(startDate.TryGetProperty("pattern", out _),
            "leap-year regex pattern on start_date must be stripped");
        Assert.False(endDate.TryGetProperty("pattern", out _),
            "leap-year regex pattern on end_date must be stripped");

        // `format: date` survives — llama.cpp has a built-in date format rule
        // that constrains the LLM output without needing the regex.
        Assert.Equal("date", startDate.GetProperty("format").GetString());
        Assert.Equal("date", endDate.GetProperty("format").GetString());

        // Core schema structure is intact
        Assert.Equal("object", sanitized.GetProperty("type").GetString());
        Assert.Equal(2, sanitized.GetProperty("required").GetArrayLength());
        Assert.True(sanitized.GetProperty("properties").TryGetProperty("query", out _));
    }


    [Fact]
    public void InjectMetaProperties_AddsThreeFieldsAndRationaleRequired()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "query": { "type": "string" }
                },
                "required": ["query"]
            }
            """).RootElement;

        var injected = McpSchemaSanitizer.InjectMetaProperties(schema);
        var props = injected.GetProperty("properties");

        Assert.True(props.TryGetProperty("_rationale", out var r));
        Assert.Equal("string", r.GetProperty("type").GetString());

        Assert.True(props.TryGetProperty("_timeout_seconds", out var t));
        Assert.Equal("integer", t.GetProperty("type").GetString());

        Assert.True(props.TryGetProperty("_background", out var b));
        Assert.Equal("boolean", b.GetProperty("type").GetString());

        var required = injected.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("query", required);
        Assert.Contains("_rationale", required);
        Assert.DoesNotContain("_timeout_seconds", required);
        Assert.DoesNotContain("_background", required);
    }

    [Fact]
    public void InjectMetaProperties_CollisionDetected_OverwritesWithMeta()
    {
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "_rationale": { "type": "integer", "description": "custom field" }
                }
            }
            """).RootElement;

        var injected = McpSchemaSanitizer.InjectMetaProperties(schema, "test-tool");
        var rationale = injected.GetProperty("properties").GetProperty("_rationale");

        // Meta interpretation takes precedence
        Assert.Equal("string", rationale.GetProperty("type").GetString());
    }

    [Fact]
    public void StripMetaFields_RemovesMetaKeysOnly()
    {
        var args = ToolInput.Create("query", "test", "_rationale", "searching for docs", "_timeout_seconds", 300, "_background", false);

        var stripped = McpSchemaSanitizer.StripMetaFields(args)!;

        Assert.Single(stripped);
        Assert.Equal("test", stripped["query"]);
    }

    [Fact]
    public void StripMetaFields_NoMetaKeys_ReturnsSameInstance()
    {
        var args = ToolInput.Create("query", "test", "limit", 10);

        var stripped = McpSchemaSanitizer.StripMetaFields(args)!;
        Assert.Same(args, stripped);
    }

    [Fact]
    public void StripMetaFields_Null_ReturnsNull()
    {
        Assert.Null(McpSchemaSanitizer.StripMetaFields(null));
    }

    [Fact]
    public void McpToolAdapter_Schema_IncludesMetaProperties()
    {
        var fakeTool = AIFunctionFactory.Create((string query) => "result", "search_memories");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "search_memories");

        var schema = adapter.ParameterSchema;
        var props = schema.GetProperty("properties");

        Assert.True(props.TryGetProperty("_rationale", out _));
        Assert.True(props.TryGetProperty("_timeout_seconds", out _));
        Assert.True(props.TryGetProperty("_background", out _));
    }

    [Fact]
    public async Task McpToolAdapter_StripsMetaFields_BeforeMcpInvocation()
    {
        var invoker = new RecordingMcpToolInvoker("result");
        string FakeFunc() => throw new InvalidOperationException("should not run");
        var fakeTool = AIFunctionFactory.Create((Func<string>)FakeFunc, "search_memories");
        var adapter = new McpToolAdapter(fakeTool, "memorizer", "search_memories", invoker: invoker);

        var context = TestToolExecutionContext.CreateBound("chan/thread", null, TrustAudience.Personal);
        var args = ToolInput.Create("query", "Akka.NET", "_rationale", "looking up docs", "_timeout_seconds", 30);

        await adapter.ExecuteAsync(args, context, CancellationToken.None);

        Assert.NotNull(invoker.Arguments);
        Assert.False(invoker.Arguments!.ContainsKey("_rationale"));
        Assert.False(invoker.Arguments!.ContainsKey("_timeout_seconds"));
        Assert.True(invoker.Arguments!.ContainsKey("query"));
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
