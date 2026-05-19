// -----------------------------------------------------------------------
// <copyright file="McpArgumentNormalizer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Normalises tool-call argument dictionaries before they're handed to an
/// <see cref="AIFunction"/> for invocation on an MCP server.
/// </summary>
/// <remarks>
/// <para>
/// Some upstream LLM SDK adapters (observed with the Anthropic .NET SDK
/// surfacing <c>tool_use</c> input) deliver nested JSON arrays and objects
/// as their <em>JSON-encoded string</em> instead of preserving them as
/// structured <see cref="JsonElement"/> values. When those arguments then
/// flow into an MCP <c>tools/call</c> request, the receiving server sees a
/// <c>string</c> in a slot the schema declares as <c>array</c> or
/// <c>object</c> and rejects the call with a validation error
/// (<c>MCP error -32602: Input validation error: expected array</c>).
/// </para>
/// <para>
/// This normalizer inspects the function's declared JSON Schema, and for
/// any argument whose schema says <c>array</c> or <c>object</c> but whose
/// value arrived as a <see cref="string"/> that parses as the matching JSON
/// shape, replaces it with the parsed <see cref="JsonElement"/>. The fix
/// is defensive: if the schema can't be read, if the value doesn't look
/// like JSON, or if parsing fails, the original value is preserved.
/// </para>
/// </remarks>
internal static class McpArgumentNormalizer
{
    /// <summary>
    /// Returns an argument dictionary with any stringified array/object
    /// values reconstituted into their structured form per the function's
    /// JSON Schema. Returns the input dictionary unchanged when no
    /// normalisation is needed.
    /// </summary>
    public static IDictionary<string, object?> Normalize(
        AIFunction function,
        IDictionary<string, object?> arguments)
        => NormalizeWithSchema(function.JsonSchema, arguments);

    /// <summary>
    /// Schema-only overload used by tests and any caller that already has
    /// the raw JSON Schema element handy.
    /// </summary>
    internal static IDictionary<string, object?> NormalizeWithSchema(
        JsonElement schema,
        IDictionary<string, object?> arguments)
    {
        var properties = TryGetSchemaProperties(schema);
        if (properties.ValueKind != JsonValueKind.Object)
            return arguments;

        Dictionary<string, object?>? rewritten = null;

        foreach (var (name, value) in arguments)
        {
            if (value is not string stringValue || string.IsNullOrEmpty(stringValue))
                continue;

            var expectedKind = GetExpectedContainerKind(properties, name);
            if (expectedKind is null)
                continue;

            if (!TryParseJsonContainer(stringValue, out var parsed) || parsed.ValueKind != expectedKind)
                continue;

            rewritten ??= new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
            rewritten[name] = parsed;
        }

        return rewritten ?? arguments;
    }

    private static JsonElement TryGetSchemaProperties(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return default;

        return schema.TryGetProperty("properties", out var properties)
            ? properties
            : default;
    }

    private static JsonValueKind? GetExpectedContainerKind(JsonElement properties, string parameterName)
    {
        if (!properties.TryGetProperty(parameterName, out var parameterSchema))
            return null;

        if (parameterSchema.ValueKind != JsonValueKind.Object)
            return null;

        if (!parameterSchema.TryGetProperty("type", out var typeElement))
            return null;

        switch (typeElement.ValueKind)
        {
            case JsonValueKind.String:
                return MapTypeName(typeElement.GetString());

            case JsonValueKind.Array:
                foreach (var entry in typeElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                        continue;

                    var mapped = MapTypeName(entry.GetString());
                    if (mapped is not null)
                        return mapped;
                }

                return null;

            default:
                return null;
        }
    }

    private static JsonValueKind? MapTypeName(string? typeName) => typeName switch
    {
        "array" => JsonValueKind.Array,
        "object" => JsonValueKind.Object,
        _ => null
    };

    private static bool TryParseJsonContainer(string value, out JsonElement element)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '[' && trimmed[0] != '{'))
        {
            element = default;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }
}
