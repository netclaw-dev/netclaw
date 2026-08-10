// -----------------------------------------------------------------------
// <copyright file="McpSchemaSanitizer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Tools;

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
    /// Coerces tool-call argument values toward the types the MCP tool's
    /// declared input schema specifies, immediately before dispatch.
    /// </summary>
    /// <remarks>
    /// The declared schema is the sole authority: a value is only ever coerced
    /// <em>toward</em> the type its parameter declares, never on a guess from
    /// the value's runtime shape. A stringified <c>array</c>/<c>object</c> is
    /// reconstructed; a string is parsed to a scalar only when the schema
    /// declares that scalar; a <c>string</c>-typed parameter is left untouched;
    /// a parameter the schema does not constrain is passed through unchanged.
    /// Returns a new dictionary — the input is never mutated, so argument
    /// values an authorization decision already evaluated cannot change.
    /// </remarks>
    public static IDictionary<string, object?>? CoerceArguments(
        IDictionary<string, object?>? arguments,
        JsonElement schema)
    {
        if (arguments is null)
            return null;

        var properties = TryGetSchemaProperties(schema);

        var coerced = new Dictionary<string, object?>(arguments.Count);
        foreach (var (key, value) in arguments)
        {
            coerced[key] = CoerceValue(value, ResolveDeclaredKinds(properties, key));
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

        var properties = TryGetSchemaProperties(parameterSchema);
        if (properties.ValueKind != JsonValueKind.Object)
            return arguments;

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
                // Strip $schema meta-reference — not a tool parameter, and it
                // breaks llama.cpp's JSON-schema-to-GBNF grammar conversion.
                "$schema" => (object?)null,

                // Strip keywords llama.cpp's json_schema_to_grammar cannot
                // express in GBNF. `pattern`/`patternProperties`/`propertyNames`
                // crash `_visit_pattern()` on pathological regex (unbounded rule
                // expansion); `not`/`if`/`then`/`else`/`multipleOf` are fatal
                // "Unrecognized schema"; content* are silently dropped. Keep
                // $ref/$defs, oneOf/anyOf/allOf, format, enum, and length/
                // range bounds — converter handles those and real MCP tools
                // rely on them.
                "pattern"
                    or "patternProperties"
                    or "propertyNames"
                    or "not"
                    or "if"
                    or "then"
                    or "else"
                    or "multipleOf"
                    or "contentEncoding"
                    or "contentMediaType"
                    or "contentSchema" => (object?)null,

                // Handle type arrays like ["string", "null"] -> "string"
                "type" when property.Value.ValueKind == JsonValueKind.Array =>
                    SimplifyTypeArray(property.Value),

                // Normalize additionalProperties: {} to true. An empty object is
                // semantically equivalent to true in JSON Schema, but confuses
                // grammar generators that expect a boolean.
                "additionalProperties" when property.Value.ValueKind == JsonValueKind.Object
                    && !property.Value.EnumerateObject().Any()
                    => true,

                // Recursively sanitize properties object
                "properties" => SanitizePropertiesObject(property.Value),

                // Recursively sanitize items (for array types)
                "items" => JsonElementToObject(SanitizeElement(property.Value)),

                // Recursively sanitize anyOf/oneOf/allOf
                "anyOf" or "oneOf" or "allOf" => SanitizeSchemaArray(property.Value),

                // Recursively sanitize all other properties so stripping
                // rules (e.g. $schema) apply through unrecognized keywords
                // like patternProperties, not, if/then/else, etc.
                _ => JsonElementToObject(SanitizeElement(property.Value))
            };

            // Skip stripped fields (e.g. $schema)
            if (value is null && property.Value.ValueKind != JsonValueKind.Null)
                continue;

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

    // ── Meta property injection ──

    private static readonly string[] MetaFieldNames = ToolCallMeta.MetaFieldNames;

    private static readonly Dictionary<string, object?> MetaRationale = new()
    {
        ["type"] = "string",
        ["description"] = "State your intent for this tool call in one sentence — what are you trying to accomplish and why?"
    };

    private static readonly Dictionary<string, object?> MetaTimeoutSeconds = new()
    {
        ["type"] = "integer",
        ["description"] = "Requested timeout in seconds. Only set when the default is insufficient."
    };

    private static readonly Dictionary<string, object?> MetaBackground = new()
    {
        ["type"] = "boolean",
        ["description"] = "Set to true to run this tool in the background and receive results later."
    };

    /// <summary>
    /// Injects <c>_rationale</c>, <c>_timeout_seconds</c>, and <c>_background</c> meta
    /// properties into an MCP tool schema. Logs a warning for any collision with existing
    /// properties. Adds <c>_rationale</c> to the required array.
    /// </summary>
    public static JsonElement InjectMetaProperties(JsonElement schema, string? toolName = null, ILogger? logger = null)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return schema;

        var dict = JsonElementToDict(schema);

        // Get or create properties object
        if (!dict.TryGetValue("properties", out var propsObj) || propsObj is not Dictionary<string, object?> props)
        {
            props = [];
            dict["properties"] = props;
        }

        // Inject meta fields with collision detection
        InjectField(props, "_rationale", MetaRationale, toolName, logger);
        InjectField(props, "_timeout_seconds", MetaTimeoutSeconds, toolName, logger);
        InjectField(props, "_background", MetaBackground, toolName, logger);

        // Add _rationale to required array
        if (dict.TryGetValue("required", out var reqObj) && reqObj is List<object?> required)
        {
            if (!required.Any(r => r is string s && s == "_rationale"))
                required.Add("_rationale");
        }
        else
        {
            dict["required"] = new List<object?> { "_rationale" };
        }

        return JsonSerializer.SerializeToElement(dict);
    }

    private static void InjectField(
        Dictionary<string, object?> props, string fieldName,
        Dictionary<string, object?> fieldSchema, string? toolName, ILogger? logger)
    {
        if (props.ContainsKey(fieldName))
        {
            logger?.LogWarning(
                "MCP tool {ToolName} already defines parameter '{FieldName}' — meta interpretation takes precedence",
                toolName ?? "unknown", fieldName);
        }

        props[fieldName] = new Dictionary<string, object?>(fieldSchema);
    }

    /// <summary>
    /// Strips meta fields (<c>_rationale</c>, <c>_timeout_seconds</c>, <c>_background</c>)
    /// from an argument dictionary before forwarding to MCP server.
    /// </summary>
    public static IDictionary<string, object?>? StripMetaFields(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;

        var hasMetaFields = false;
        foreach (var key in MetaFieldNames)
        {
            if (arguments.ContainsKey(key))
            {
                hasMetaFields = true;
                break;
            }
        }

        if (!hasMetaFields)
            return arguments;

        var clean = new Dictionary<string, object?>(arguments.Count);
        foreach (var (key, value) in arguments)
        {
            if (!MetaFieldNames.Contains(key))
                clean[key] = value;
        }

        return clean;
    }

    // ── Argument coercion ──

    /// <summary>
    /// The JSON value kinds a parameter's schema may declare, as a set so that
    /// union types (e.g. <c>["array", "null"]</c>) resolve cleanly.
    /// <see cref="SchemaKinds.None"/> means the schema does not constrain the
    /// parameter's type.
    /// </summary>
    [Flags]
    private enum SchemaKinds
    {
        None = 0,
        String = 1,
        Integer = 1 << 1,
        Number = 1 << 2,
        Boolean = 1 << 3,
        Array = 1 << 4,
        Object = 1 << 5,
    }

    private static JsonElement TryGetSchemaProperties(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            return properties;
        }

        return default;
    }

    private static SchemaKinds ResolveDeclaredKinds(JsonElement properties, string parameterName)
    {
        // Undeclared: no properties block, the parameter is absent, its schema
        // is an empty `{}`, or it is typed only via $ref/anyOf/oneOf/allOf
        // (no `type` key). Undeclared parameters pass through uncoerced — the
        // schema gave no type to coerce toward.
        if (properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty(parameterName, out var parameterSchema)
            || parameterSchema.ValueKind != JsonValueKind.Object
            || !parameterSchema.TryGetProperty("type", out var typeElement))
        {
            return SchemaKinds.None;
        }

        switch (typeElement.ValueKind)
        {
            case JsonValueKind.String:
                return MapTypeName(typeElement.GetString());

            case JsonValueKind.Array:
                var kinds = SchemaKinds.None;
                foreach (var entry in typeElement.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String)
                        kinds |= MapTypeName(entry.GetString());
                }

                return kinds;

            default:
                return SchemaKinds.None;
        }
    }

    private static SchemaKinds MapTypeName(string? typeName) => typeName switch
    {
        "string" => SchemaKinds.String,
        "integer" => SchemaKinds.Integer,
        "number" => SchemaKinds.Number,
        "boolean" => SchemaKinds.Boolean,
        "array" => SchemaKinds.Array,
        "object" => SchemaKinds.Object,
        _ => SchemaKinds.None,
    };

    private static object? CoerceValue(object? value, SchemaKinds declared)
    {
        // The schema does not constrain this parameter — forward the value
        // exactly as the model emitted it. Coercing here would be a shape
        // guess, not a schema-directed transform.
        if (declared == SchemaKinds.None)
            return value;

        // Coercion only ever acts on a value that arrived as a string — either
        // a CLR string or a JsonElement of ValueKind.String. Anything else is
        // already structured/typed and is trusted as-is.
        var stringForm = AsStringValue(value);
        if (stringForm is null)
            return value;

        // The schema already accepts a string here, so the value is valid
        // as-is — coercing it would invent a type the model did not choose.
        if ((declared & SchemaKinds.String) != 0)
            return value;

        // The model emitted a container as a JSON-encoded string. Reconstruct
        // it, but only when the parsed kind matches a kind the schema declares
        // — never coerce across kinds.
        if ((declared & (SchemaKinds.Array | SchemaKinds.Object)) != 0
            && TryParseJsonContainer(stringForm, out var parsed))
        {
            var parsedKind = parsed.ValueKind switch
            {
                JsonValueKind.Array => SchemaKinds.Array,
                JsonValueKind.Object => SchemaKinds.Object,
                _ => SchemaKinds.None,
            };

            if (parsedKind != SchemaKinds.None && (declared & parsedKind) != 0)
                return parsed;
        }

        // The schema declares a scalar and the model emitted it as a string —
        // parse it toward exactly that scalar. This is the only string→scalar
        // path, and it fires only because the schema asked for it.
        if ((declared & SchemaKinds.Integer) != 0 && TryParseJsonInteger(stringForm, out var integer))
            return integer;

        if ((declared & SchemaKinds.Number) != 0 && TryParseJsonNumber(stringForm, out var number))
            return number;

        if ((declared & SchemaKinds.Boolean) != 0 && bool.TryParse(stringForm, out var boolean))
            return boolean;

        // The value did not parse as the declared type: forward it unchanged.
        // A genuine mismatch is rejected loudly by the MCP server's own schema
        // validation, never silently masked here.
        return value;
    }

    private static string? AsStringValue(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null,
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

    private static bool TryParseJsonNumber(string value, out object number)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            number = longValue;
            return true;
        }

        // JSON numbers are culture-invariant; parse with the invariant culture
        // so a comma-decimal host locale cannot change the result. Reject
        // non-finite values — JSON has no NaN/Infinity literal.
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue)
            && double.IsFinite(doubleValue))
        {
            number = doubleValue;
            return true;
        }

        number = 0L;
        return false;
    }

    private static bool TryParseJsonInteger(string value, out long number)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
}
