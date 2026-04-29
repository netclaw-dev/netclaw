// -----------------------------------------------------------------------
// <copyright file="ToolCallMeta.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Tools;

/// <summary>
/// Per-call metadata envelope extracted from tool call arguments before dispatch.
/// Injected into tool schemas as <c>_rationale</c>, <c>_timeout_seconds</c>,
/// and <c>_background</c>; persisted as opaque JSON on
/// <c>SerializableToolCall.MetaJson</c>.
/// </summary>
public sealed record ToolCallMeta
{
    public static readonly string[] MetaFieldNames = ["_rationale", "_timeout_seconds", "_background"];

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("timeout_seconds")]
    public int? TimeoutHintSeconds { get; init; }

    [JsonPropertyName("background")]
    public bool Background { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ToolCallMeta? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ToolCallMeta>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts meta fields from an argument dictionary, returning the parsed meta
    /// and a cleaned dictionary with meta keys removed. Shared by persistence and
    /// pipeline extraction paths.
    /// </summary>
    public static (ToolCallMeta? Meta, IDictionary<string, object?>? CleanArgs) ExtractFrom(
        IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return (null, arguments);

        string? rationale = null;
        int? timeoutSeconds = null;
        bool background = false;
        var hasAnyMeta = false;

        if (arguments.TryGetValue("_rationale", out var rVal) && rVal is not null)
        {
            rationale = rVal switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => rVal.ToString()
            };
            hasAnyMeta = true;
        }

        if (arguments.TryGetValue("_timeout_seconds", out var tVal) && tVal is not null)
        {
            timeoutSeconds = tVal switch
            {
                int i when i > 0 => i,
                long l when l > 0 => (int)l,
                double d when d > 0 => (int)d,
                JsonElement { ValueKind: JsonValueKind.Number } je when je.GetInt32() > 0 => je.GetInt32(),
                string s when int.TryParse(s, out var parsed) && parsed > 0 => parsed,
                _ => null
            };
            if (timeoutSeconds.HasValue)
                hasAnyMeta = true;
        }

        if (arguments.TryGetValue("_background", out var bVal) && bVal is not null)
        {
            background = bVal switch
            {
                bool b => b,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => false
            };
            if (background)
                hasAnyMeta = true;
        }

        if (!hasAnyMeta)
            return (null, arguments);

        var clean = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var kvp in arguments)
        {
            if (!MetaFieldNames.Contains(kvp.Key))
                clean[kvp.Key] = kvp.Value;
        }

        var meta = new ToolCallMeta
        {
            Rationale = rationale,
            TimeoutHintSeconds = timeoutSeconds,
            Background = background
        };

        return (meta, clean);
    }
}
