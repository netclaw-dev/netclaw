using System.Text.Json;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Sanitizes MCP tool JSON schemas and coerces arguments for LLM compatibility.
/// </summary>
/// <remarks>
/// Some LLM providers (notably Ollama) don't handle certain JSON Schema features:
/// <list type="bullet">
/// <item>Nullable type arrays like <c>{"type": ["string", "null"]}</c></item>
/// <item>Complex union types in anyOf/oneOf/allOf</item>
/// </list>
/// Additionally, some providers send numbers as strings in tool call arguments.
/// This class provides static helpers for both schema sanitization and argument coercion.
/// </remarks>
public static class McpSchemaSanitizer
{
    /// <summary>
    /// Sanitizes a JSON schema, simplifying nullable type arrays and recursively
    /// cleaning nested schemas.
    /// </summary>
    public static JsonElement SanitizeSchema(JsonElement schema)
    {
        using var doc = JsonDocument.Parse(SanitizeElement(schema).GetRawText());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Coerces tool call arguments to their expected types.
    /// Some LLMs (like Ollama) send numbers as strings (e.g., "10" instead of 10).
    /// </summary>
    public static IDictionary<string, object?>? CoerceArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;

        var coerced = new Dictionary<string, object?>(arguments.Count);
        foreach (var (key, value) in arguments)
        {
            coerced[key] = CoerceValue(value);
        }

        return coerced;
    }

    /// <summary>
    /// Normalizes argument keys against schema property names using
    /// case-insensitive matching (e.g. Url -&gt; url).
    /// </summary>
    public static IDictionary<string, object?>? NormalizeArgumentKeys(
        IDictionary<string, object?>? arguments,
        JsonElement parameterSchema)
    {
        if (arguments is null)
            return null;

        if (parameterSchema.ValueKind != JsonValueKind.Object
            || !parameterSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        var canonicalKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties.EnumerateObject())
        {
            canonicalKeys[property.Name] = property.Name;
        }

        if (canonicalKeys.Count == 0)
            return arguments;

        var normalized = new Dictionary<string, object?>(arguments.Count);
        foreach (var (key, value) in arguments)
        {
            if (canonicalKeys.TryGetValue(key, out var canonical))
            {
                normalized[canonical] = value;
            }
            else
            {
                normalized[key] = value;
            }
        }

        return normalized;
    }

    // ── Schema sanitization ──

    private static JsonElement SanitizeElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => SanitizeObject(element),
            JsonValueKind.Array => SanitizeArray(element),
            _ => element.Clone()
        };
    }

    private static JsonElement SanitizeObject(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>();

        foreach (var property in obj.EnumerateObject())
        {
            var value = property.Name switch
            {
                // Handle type arrays like ["string", "null"] -> "string"
                "type" when property.Value.ValueKind == JsonValueKind.Array =>
                    SimplifyTypeArray(property.Value),

                // Recursively sanitize properties object
                "properties" => SanitizePropertiesObject(property.Value),

                // Recursively sanitize items (for array types)
                "items" => JsonElementToObject(SanitizeElement(property.Value)),

                // Recursively sanitize anyOf/oneOf/allOf
                "anyOf" or "oneOf" or "allOf" => SanitizeSchemaArray(property.Value),

                // Pass through other properties
                _ => JsonElementToObject(property.Value)
            };

            dict[property.Name] = value;
        }

        return JsonSerializer.SerializeToElement(dict);
    }

    private static JsonElement SanitizeArray(JsonElement array)
    {
        var list = new List<object?>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(JsonElementToObject(SanitizeElement(item)));
        }

        return JsonSerializer.SerializeToElement(list);
    }

    /// <summary>
    /// Simplifies type arrays like ["string", "null"] to just "string".
    /// </summary>
    private static object SimplifyTypeArray(JsonElement typeArray)
    {
        var types = new List<string>();

        foreach (var typeItem in typeArray.EnumerateArray())
        {
            if (typeItem.ValueKind == JsonValueKind.String)
            {
                var typeStr = typeItem.GetString();
                if (typeStr != null && typeStr != "null")
                {
                    types.Add(typeStr);
                }
            }
        }

        return types.Count switch
        {
            0 => "string", // Fallback if all types were null
            1 => types[0],
            _ => types // Multiple non-null types — return as array
        };
    }

    private static object? SanitizePropertiesObject(JsonElement properties)
    {
        if (properties.ValueKind != JsonValueKind.Object)
            return JsonElementToObject(properties);

        var dict = new Dictionary<string, object?>();
        foreach (var prop in properties.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToObject(SanitizeElement(prop.Value));
        }

        return dict;
    }

    private static object? SanitizeSchemaArray(JsonElement schemaArray)
    {
        if (schemaArray.ValueKind != JsonValueKind.Array)
            return JsonElementToObject(schemaArray);

        var list = new List<object?>();
        foreach (var schema in schemaArray.EnumerateArray())
        {
            list.Add(JsonElementToObject(SanitizeElement(schema)));
        }

        return list;
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => JsonElementToDict(element),
            JsonValueKind.Array => JsonElementToList(element),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static Dictionary<string, object?> JsonElementToDict(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToObject(prop.Value);
        }

        return dict;
    }

    private static List<object?> JsonElementToList(JsonElement element)
    {
        var list = new List<object?>();
        foreach (var item in element.EnumerateArray())
        {
            list.Add(JsonElementToObject(item));
        }

        return list;
    }

    // ── Argument coercion ──

    private static object? CoerceValue(object? value)
    {
        return value switch
        {
            string s => CoerceStringValue(s),
            JsonElement je => ConvertJsonValue(je),
            IList<object?> list => list.Select(CoerceValue).ToList(),
            IDictionary<string, object?> dict => dict.ToDictionary(
                kvp => kvp.Key, kvp => CoerceValue(kvp.Value)),
            _ => value
        };
    }

    /// <summary>
    /// Coerces string values to appropriate types when possible.
    /// Some LLMs (like Ollama) incorrectly pass numbers as strings.
    /// </summary>
    private static object? CoerceStringValue(string? str)
    {
        if (str == null)
            return null;

        if (long.TryParse(str, out var longVal))
            return longVal;

        if (double.TryParse(str, out var doubleVal))
            return doubleVal;

        if (str.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (str.Equals("false", StringComparison.OrdinalIgnoreCase))
            return false;

        return str;
    }

    private static object? ConvertJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => CoerceStringValue(value.GetString()),
            JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToList(),
            JsonValueKind.Object => ConvertJsonObject(value),
            _ => value.GetRawText()
        };
    }

    private static Dictionary<string, object?> ConvertJsonObject(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonValue(prop.Value);
        }

        return dict;
    }
}
