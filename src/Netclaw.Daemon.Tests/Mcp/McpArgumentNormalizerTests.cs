// -----------------------------------------------------------------------
// <copyright file="McpArgumentNormalizerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Daemon.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpArgumentNormalizerTests
{
    private static JsonElement Schema(string schemaJson)
        => JsonDocument.Parse(schemaJson).RootElement;

    [Fact]
    public void Normalize_ArrayOfObjectsAsString_RebuildsAsJsonArrayElement()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);

        var args = new Dictionary<string, object?>
        {
            ["tasks"] = "[{\"content\":\"A\"},{\"content\":\"B\"}]"
        };

        var result = McpArgumentNormalizer.NormalizeWithSchema(schema, args);

        Assert.IsType<JsonElement>(result["tasks"]);
        var array = (JsonElement)result["tasks"]!;
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.Equal(2, array.GetArrayLength());
        Assert.Equal("A", array[0].GetProperty("content").GetString());
        Assert.Equal("B", array[1].GetProperty("content").GetString());
    }

    [Fact]
    public void Normalize_ObjectAsString_RebuildsAsJsonObjectElement()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "payload": { "type": "object" }
              }
            }
            """);

        var args = new Dictionary<string, object?>
        {
            ["payload"] = "{\"foo\":42,\"bar\":\"baz\"}"
        };

        var result = McpArgumentNormalizer.NormalizeWithSchema(schema, args);

        var obj = Assert.IsType<JsonElement>(result["payload"]);
        Assert.Equal(JsonValueKind.Object, obj.ValueKind);
        Assert.Equal(42, obj.GetProperty("foo").GetInt32());
        Assert.Equal("baz", obj.GetProperty("bar").GetString());
    }

    [Fact]
    public void Normalize_NullableTypeArray_StillRecognisedAsArray()
    {
        // Schema with union type — common when JSON Schema generators emit
        // [ "array", "null" ] to allow optional array values.
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tags": { "type": ["array", "null"], "items": { "type": "string" } }
              }
            }
            """);

        var args = new Dictionary<string, object?>
        {
            ["tags"] = "[\"a\",\"b\"]"
        };

        var result = McpArgumentNormalizer.NormalizeWithSchema(schema, args);

        var array = Assert.IsType<JsonElement>(result["tags"]);
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.Equal(2, array.GetArrayLength());
    }

    [Fact]
    public void Normalize_StringForStringSchema_LeftUnchanged()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "note": { "type": "string" }
              }
            }
            """);

        var args = new Dictionary<string, object?>
        {
            ["note"] = "hello"
        };

        var result = McpArgumentNormalizer.NormalizeWithSchema(schema, args);

        Assert.Same(args, result);
        Assert.Equal("hello", result["note"]);
    }

    [Fact]
    public void Normalize_StringForArraySchemaButInvalidJson_LeftUnchanged()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);

        var args = new Dictionary<string, object?>
        {
            ["tasks"] = "this is not json"
        };

        var result = McpArgumentNormalizer.NormalizeWithSchema(schema, args);

        Assert.Same(args, result);
        Assert.Equal("this is not json", result["tasks"]);
    }

    [Fact]
    public void Normalize_StringWhoseParsedKindDoesNotMatchSchema_LeftUnchanged()
    {
        // Schema says array; the string happens to parse as a JSON object —
        // refuse to coerce (we only restore the structured form when the
        // parsed kind matches the schema declaration).
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);

        var args = new Dictionary<string, object?>
        {
            ["tasks"] = "{\"oops\":\"this should have been an array\"}"
        };

        var result = McpArgumentNormalizer.NormalizeWithSchema(schema, args);

        Assert.Same(args, result);
        Assert.Equal("{\"oops\":\"this should have been an array\"}", result["tasks"]);
    }

    [Fact]
    public void Normalize_ExistingJsonElement_NotMolested()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);

        var preExisting = JsonDocument.Parse("[{\"content\":\"A\"}]").RootElement.Clone();
        var args = new Dictionary<string, object?>
        {
            ["tasks"] = preExisting
        };

        var result = McpArgumentNormalizer.NormalizeWithSchema(schema, args);

        Assert.Same(args, result);
        Assert.Equal(JsonValueKind.Array, ((JsonElement)result["tasks"]!).ValueKind);
    }

    [Fact]
    public void Normalize_SchemaWithoutProperties_ReturnsInputUnchanged()
    {
        var schema = Schema("""
            { "type": "object" }
            """);

        var args = new Dictionary<string, object?>
        {
            ["tasks"] = "[{\"content\":\"A\"}]"
        };

        var result = McpArgumentNormalizer.NormalizeWithSchema(schema, args);

        Assert.Same(args, result);
    }

    [Fact]
    public void Normalize_PreservesOtherUntouchedValues()
    {
        var schema = Schema("""
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object" } },
                "dryRun": { "type": "boolean" },
                "count": { "type": "integer" }
              }
            }
            """);

        var args = new Dictionary<string, object?>
        {
            ["tasks"] = "[{\"content\":\"A\"}]",
            ["dryRun"] = true,
            ["count"] = 5
        };

        var result = McpArgumentNormalizer.NormalizeWithSchema(schema, args);

        Assert.NotSame(args, result);
        Assert.Equal(JsonValueKind.Array, ((JsonElement)result["tasks"]!).ValueKind);
        Assert.Equal(true, result["dryRun"]);
        Assert.Equal(5, result["count"]);
    }
}
