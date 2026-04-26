using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Extracts <see cref="ToolCallMeta"/> fields (<c>_rationale</c>, <c>_timeout_seconds</c>,
/// <c>_background</c>) from a <see cref="FunctionCallContent.Arguments"/> dictionary.
/// Returns the parsed meta and a cleaned argument dictionary with meta keys removed.
/// </summary>
internal static class ToolCallMetaExtractor
{
    private static readonly string[] MetaKeys = ["_rationale", "_timeout_seconds", "_background"];

    public static (ToolCallMeta? Meta, FunctionCallContent CleanedToolCall) Extract(FunctionCallContent tc)
    {
        var args = tc.Arguments;
        if (args is null || args.Count == 0)
            return (null, tc);

        string? rationale = null;
        int? timeoutSeconds = null;
        bool background = false;
        var hasAnyMeta = false;

        if (args.TryGetValue("_rationale", out var rVal) && rVal is not null)
        {
            rationale = rVal switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => rVal.ToString()
            };
            hasAnyMeta = true;
        }

        if (args.TryGetValue("_timeout_seconds", out var tVal) && tVal is not null)
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

        if (args.TryGetValue("_background", out var bVal) && bVal is not null)
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
            return (null, tc);

        var clean = new Dictionary<string, object?>(args.Count, StringComparer.Ordinal);
        foreach (var kvp in args)
        {
            if (!MetaKeys.Contains(kvp.Key))
                clean[kvp.Key] = kvp.Value;
        }

        var meta = new ToolCallMeta
        {
            Rationale = rationale,
            TimeoutHintSeconds = timeoutSeconds,
            Background = background
        };

        var cleanedTc = new FunctionCallContent(tc.CallId, tc.Name, clean);
        return (meta, cleanedTc);
    }

    /// <summary>
    /// Computes the effective timeout by clamping the LLM's hint between the tool's
    /// default floor and the config ceiling.
    /// </summary>
    public static TimeSpan ComputeEffectiveTimeout(
        int? hintSeconds, TimeSpan defaultTimeout, int maxToolTimeoutSeconds)
    {
        if (!hintSeconds.HasValue || hintSeconds.Value <= 0)
            return defaultTimeout;

        var floorSeconds = (int)defaultTimeout.TotalSeconds;
        if (hintSeconds.Value < floorSeconds)
            return defaultTimeout;

        var clamped = Math.Min(hintSeconds.Value, maxToolTimeoutSeconds);
        return TimeSpan.FromSeconds(clamped);
    }
}
