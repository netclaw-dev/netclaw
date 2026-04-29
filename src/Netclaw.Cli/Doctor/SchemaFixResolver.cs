// -----------------------------------------------------------------------
// <copyright file="SchemaFixResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Schema-driven config fix resolver. Runs JSON Schema validation against the config,
/// then pattern-matches known error types to apply safe, idempotent fixes.
/// </summary>
public static class SchemaFixResolver
{
    /// <summary>
    /// Validates config against schema and applies safe fixes for known error patterns.
    /// Returns true if any fixes were applied; <paramref name="appliedFixes"/> lists descriptions.
    /// </summary>
    public static bool TryApplySchemaFixes(
        JsonSchema schema,
        JsonObject schemaJson,
        JsonObject config,
        out List<string> appliedFixes)
    {
        appliedFixes = [];

        var evaluation = schema.Evaluate(config, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (evaluation.IsValid)
            return false;

        if (evaluation.Details is null)
            return false;

        var failingDetails = evaluation.Details
            .Where(d => !d.IsValid && d.Errors is not null)
            .ToList();

        var changed = false;
        changed |= TryFixIntegerEnumValues(schemaJson, config, failingDetails, appliedFixes);
        changed |= TryRemoveDisallowedProperties(schemaJson, config, failingDetails, appliedFixes);
        changed |= TryInsertMissingDefaults(schemaJson, config, failingDetails, appliedFixes);
        return changed;
    }

    /// <summary>
    /// Fixes integer values where the schema expects a string enum.
    /// Common cause: C# enums serialized as their numeric value instead of name.
    /// </summary>
    private static bool TryFixIntegerEnumValues(
        JsonObject schemaJson,
        JsonObject config,
        List<EvaluationResults> failingDetails,
        List<string> appliedFixes)
    {
        var changed = false;

        foreach (var detail in failingDetails)
        {
            if (detail.Errors is not { } errors)
                continue;

            // Must have both type and enum errors — indicates integer where string enum expected
            if (!errors.ContainsKey("type") || !errors.ContainsKey("enum"))
                continue;

            var instancePath = detail.InstanceLocation.ToString();
            if (string.IsNullOrEmpty(instancePath))
                continue;

            var segments = ParseJsonPointer(instancePath);
            if (segments.Length == 0)
                continue;

            if (ResolveConfigValue(config, segments) is not JsonValue jsonVal
                || !jsonVal.TryGetValue<int>(out var intValue))
                continue;

            var propertySchema = ResolvePropertySchema(schemaJson, segments);
            if (propertySchema?["enum"] is not JsonArray enumArray || enumArray.Count == 0)
                continue;

            // Map integer index to enum string — C# enums serialize as 0-based ordinals
            if (intValue < 0 || intValue >= enumArray.Count)
                continue;

            var enumStr = enumArray[intValue]?.GetValue<string>();
            if (enumStr is null)
                continue;

            if (SetConfigValue(config, segments, JsonValue.Create(enumStr)))
            {
                appliedFixes.Add($"{instancePath}: {intValue} → \"{enumStr}\"");
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Removes properties that are disallowed by <c>additionalProperties: false</c>.
    /// Common cause: a property was removed from the schema in a newer version.
    /// </summary>
    /// <remarks>
    /// When <c>additionalProperties: false</c> rejects a property, json-everything emits
    /// an error detail at the full instance path of the rejected property with an empty
    /// string error key and evaluation path ending in <c>/additionalProperties</c>.
    /// </remarks>
    private static bool TryRemoveDisallowedProperties(
        JsonObject schemaJson,
        JsonObject config,
        List<EvaluationResults> failingDetails,
        List<string> appliedFixes)
    {
        var changed = false;

        foreach (var detail in failingDetails)
        {
            // Detect additionalProperties: false violations by evaluation path
            var evalPath = detail.EvaluationPath.ToString();
            if (!evalPath.EndsWith("/additionalProperties", StringComparison.Ordinal))
                continue;

            var instancePath = detail.InstanceLocation.ToString();
            if (string.IsNullOrEmpty(instancePath))
                continue;

            var segments = ParseJsonPointer(instancePath);
            if (segments.Length < 2)
                continue;

            // Verify this property is genuinely not in the schema
            var propertySchema = ResolvePropertySchema(schemaJson, segments);
            if (propertySchema is not null)
                continue; // schema recognizes this property — don't remove

            // Remove the property from its parent
            var parentSegments = segments[..^1];
            var propertyName = segments[^1];
            if (ResolveConfigNode(config, parentSegments) is JsonObject parent
                && parent.ContainsKey(propertyName))
            {
                parent.Remove(propertyName);
                appliedFixes.Add($"Removed disallowed property {instancePath}");
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Inserts default values for missing required properties when the schema defines a default.
    /// </summary>
    private static bool TryInsertMissingDefaults(
        JsonObject schemaJson,
        JsonObject config,
        List<EvaluationResults> failingDetails,
        List<string> appliedFixes)
    {
        var changed = false;

        foreach (var detail in failingDetails)
        {
            if (detail.Errors is not { } errors)
                continue;

            if (!errors.ContainsKey("required"))
                continue;

            var instancePath = detail.InstanceLocation.ToString();
            var parentSegments = string.IsNullOrEmpty(instancePath)
                ? []
                : ParseJsonPointer(instancePath);

            var parentSchema = parentSegments.Length == 0
                ? schemaJson
                : ResolvePropertySchema(schemaJson, parentSegments);

            if (parentSchema is null)
                continue;

            var requiredProps = parentSchema["required"] is JsonArray reqArr
                ? reqArr.Select(n => n?.GetValue<string>()).Where(s => s is not null).ToHashSet()
                : [];

            var properties = parentSchema["properties"] as JsonObject;
            if (properties is null || requiredProps.Count == 0)
                continue;

            var configParent = parentSegments.Length == 0
                ? config
                : ResolveConfigNode(config, parentSegments) as JsonObject;

            if (configParent is null)
                continue;

            foreach (var propName in requiredProps)
            {
                if (configParent.ContainsKey(propName!))
                    continue; // already present

                var propSchema = FollowRef(schemaJson, properties[propName!] as JsonObject);
                if (propSchema?["default"] is not { } defaultNode)
                    continue;

                configParent[propName!] = defaultNode.DeepClone();
                var fullPath = parentSegments.Length == 0
                    ? $"/{propName}"
                    : $"{instancePath}/{propName}";
                appliedFixes.Add($"Inserted default for {fullPath}");
                changed = true;
            }
        }

        return changed;
    }

    #region Schema navigation helpers

    /// <summary>
    /// Navigate the schema JSON object to resolve the sub-schema for a given config path.
    /// Handles <c>properties</c>, <c>additionalProperties</c>, <c>items</c>, and <c>$ref</c>.
    /// </summary>
    internal static JsonObject? ResolvePropertySchema(JsonObject schemaRoot, ReadOnlySpan<string> pathSegments)
    {
        JsonObject? current = schemaRoot;

        foreach (var segment in pathSegments)
        {
            if (current is null) return null;
            current = FollowRef(schemaRoot, current);
            if (current is null) return null;

            // Try named property first
            if (current["properties"] is JsonObject props && props[segment] is JsonNode propNode)
            {
                current = FollowRef(schemaRoot, propNode as JsonObject);
                continue;
            }

            // Try additionalProperties (for dynamic keys like MCP server names)
            if (current["additionalProperties"] is JsonObject addProps)
            {
                current = FollowRef(schemaRoot, addProps);
                continue;
            }

            // Try array items
            if (current["items"] is JsonObject items
                && int.TryParse(segment, out _))
            {
                current = FollowRef(schemaRoot, items);
                continue;
            }

            return null; // can't resolve further
        }

        return current is not null ? FollowRef(schemaRoot, current) : null;
    }

    /// <summary>
    /// Resolve <c>$ref</c> to the referenced schema object. Only handles local <c>#/</c> references.
    /// </summary>
    private static JsonObject? FollowRef(JsonObject root, JsonObject? schema)
    {
        if (schema is null) return null;
        if (schema["$ref"]?.GetValue<string>() is not { } refStr) return schema;
        if (!refStr.StartsWith("#/", StringComparison.Ordinal)) return schema;

        JsonNode? current = root;
        foreach (var part in refStr[2..].Split('/'))
        {
            current = (current as JsonObject)?[part];
            if (current is null) return null;
        }

        return current as JsonObject;
    }

    #endregion

    #region Config navigation helpers

    private static string[] ParseJsonPointer(string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
            return [];

        return pointer.TrimStart('/').Split('/');
    }

    private static JsonNode? ResolveConfigNode(JsonObject root, ReadOnlySpan<string> segments)
    {
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray arr when int.TryParse(segment, out var idx) => arr[idx],
                _ => null
            };
            if (current is null) return null;
        }

        return current;
    }

    private static JsonValue? ResolveConfigValue(JsonObject root, ReadOnlySpan<string> segments)
        => ResolveConfigNode(root, segments) as JsonValue;

    private static bool SetConfigValue(JsonObject root, ReadOnlySpan<string> segments, JsonNode newValue)
    {
        if (segments.Length == 0) return false;

        var parentSegments = segments[..^1];
        var propertyName = segments[^1];

        if (ResolveConfigNode(root, parentSegments) is JsonObject parent)
        {
            parent[propertyName] = newValue;
            return true;
        }

        return false;
    }

    #endregion
}
