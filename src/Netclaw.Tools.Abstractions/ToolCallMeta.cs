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
    /// <param name="resolveMeta">
    /// Maps an argument key to the canonical meta field it addresses, or null if it
    /// is not meta. When omitted, only the exact canonical names
    /// (<see cref="MetaFieldNames"/>) match — the safe, schema-blind behavior for
    /// persistence. The executor passes a schema-aware resolver
    /// (<see cref="ToolArgumentValidator.ResolveMetaField(INetclawTool, string)"/>)
    /// that recognizes ChatGPT-style near-misses (<c>TimeoutSeconds</c>,
    /// <c>Rationale</c>) but yields to a tool's own declared parameter of the same
    /// name, so a third-party MCP argument is never hijacked as meta.
    /// </param>
    public static (ToolCallMeta? Meta, IDictionary<string, object?>? CleanArgs) ExtractFrom(
        IDictionary<string, object?>? arguments, Func<string, string?>? resolveMeta = null)
    {
        if (arguments is null || arguments.Count == 0)
            return (null, arguments);

        resolveMeta ??= ResolveExactMetaField;

        string? rationale = null;
        int? timeoutSeconds = null;
        bool background = false;
        var hasAnyMeta = false;

        // Keys (in the model's own spelling) that resolve to a meta field; stripped
        // from the args the tool binder sees. Conflicting double-spellings (e.g.
        // both "_timeout_seconds" and "TimeoutSeconds") are rejected loudly upstream
        // in ToolArgumentValidator before this runs on the native path.
        HashSet<string>? metaKeys = null;

        foreach (var kvp in arguments)
        {
            var canonical = resolveMeta(kvp.Key);
            if (canonical is null)
                continue;

            (metaKeys ??= new HashSet<string>(StringComparer.Ordinal)).Add(kvp.Key);

            var value = kvp.Value;
            if (value is null)
                continue;

            switch (canonical)
            {
                case "_rationale":
                    rationale = value switch
                    {
                        string s => s,
                        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                        _ => value.ToString()
                    };
                    hasAnyMeta = true;
                    break;

                case "_timeout_seconds":
                    // Shares ToolArgumentHelper.TryCoerceInt with ValidateMetaValues so
                    // acceptance here and rejection there cannot drift. A positive int
                    // is a valid hint; anything else (including 0/negative) is left null
                    // and validation rejects it loudly before dispatch.
                    if (ToolArgumentHelper.TryCoerceInt(value, out var parsedTimeout) && parsedTimeout > 0)
                    {
                        timeoutSeconds = parsedTimeout;
                        hasAnyMeta = true;
                    }

                    break;

                case "_background":
                    // Shares ToolArgumentHelper.TryCoerceBool with ValidateMetaValues.
                    if (ToolArgumentHelper.TryCoerceBool(value, out var parsedBackground))
                    {
                        background = parsedBackground;
                        if (background)
                            hasAnyMeta = true;
                    }

                    break;

                default:
                    // A name was added to MetaFieldNames (or the near-miss table)
                    // without a matching extraction case here. Fail loudly rather
                    // than strip-but-silently-drop the value.
                    throw new InvalidOperationException(
                        $"ToolCallMeta.ExtractFrom: unhandled meta field '{canonical}'.");
            }
        }

        if (metaKeys is null)
            return (null, arguments);

        // Strip every resolved meta key — including ones that carried no usable
        // value — so the tool binder never receives a stray meta argument.
        var clean = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var kvp in arguments)
        {
            if (!metaKeys.Contains(kvp.Key))
                clean[kvp.Key] = kvp.Value;
        }

        if (!hasAnyMeta)
            return (null, clean);

        var meta = new ToolCallMeta
        {
            Rationale = rationale,
            TimeoutHintSeconds = timeoutSeconds,
            Background = background
        };

        return (meta, clean);
    }

    /// <summary>
    /// Exact (ordinal) canonical-name resolver — the schema-blind default used by
    /// persistence and any caller that cannot prove a key is not a real parameter.
    /// </summary>
    public static string? ResolveExactMetaField(string key)
        => Array.IndexOf(MetaFieldNames, key) >= 0 ? key : null;
}
