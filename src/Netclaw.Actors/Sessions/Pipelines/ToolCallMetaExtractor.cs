// -----------------------------------------------------------------------
// <copyright file="ToolCallMetaExtractor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Extracts <see cref="ToolCallMeta"/> fields from a <see cref="FunctionCallContent"/>
/// and returns a cleaned tool call with meta keys removed.
/// </summary>
internal static class ToolCallMetaExtractor
{
    public static (ToolCallMeta? Meta, FunctionCallContent CleanedToolCall) Extract(FunctionCallContent tc)
    {
        var (meta, cleanArgs) = ToolCallMeta.ExtractFrom(tc.Arguments);
        if (meta is null)
            return (null, tc);

        var cleanedTc = new FunctionCallContent(tc.CallId, tc.Name, cleanArgs);
        return (meta, cleanedTc);
    }

    /// <summary>
    /// Computes the effective timeout by clamping the LLM's hint between the tool's
    /// default floor and the config ceiling. When the effective value differs from
    /// the requested value, the returned notice MUST reach the tool result the
    /// model reads — silent clamping manufactured a false belief in production
    /// (the agent "set" 1200s, got 90s, and looped; tool-call-metadata spec).
    /// </summary>
    public static (TimeSpan Timeout, string? Notice) ComputeEffectiveTimeout(
        int? hintSeconds, TimeSpan defaultTimeout, int maxToolTimeoutSeconds)
    {
        if (!hintSeconds.HasValue || hintSeconds.Value <= 0)
            return (defaultTimeout, null);

        var floorSeconds = (int)defaultTimeout.TotalSeconds;
        if (hintSeconds.Value < floorSeconds)
        {
            return (defaultTimeout,
                $"[timeout request {hintSeconds.Value}s is below the {floorSeconds}s tool default; {floorSeconds}s applied]");
        }

        if (hintSeconds.Value > maxToolTimeoutSeconds)
        {
            return (TimeSpan.FromSeconds(maxToolTimeoutSeconds),
                $"[timeout clamped: requested {hintSeconds.Value}s, maximum {maxToolTimeoutSeconds}s applied — to run without blocking the turn, submit it with _background:true (same maximum)]");
        }

        return (TimeSpan.FromSeconds(hintSeconds.Value), null);
    }

    /// <summary>
    /// Rejects present-but-invalid meta values before dispatch. Returns null when
    /// the meta surface is valid; otherwise a model-facing error (the call must
    /// not execute — the agent expressed execution semantics we cannot honor, so
    /// we do not run on defaults instead). Computed pipeline-side so the
    /// persisted <see cref="ToolCallMeta"/> type stays unchanged. Exact key
    /// lookup mirrors <see cref="ToolCallMeta.ExtractFrom"/>.
    /// </summary>
    public static string? ValidateMetaValues(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return null;

        // Validity is defined as "the shared coercion accepts it" — the same
        // TryCoerce* ToolCallMeta.ExtractFrom binds through — so a value can
        // never validate here yet extract to null (or vice versa). A timeout
        // additionally must be positive, matching ExtractFrom's `> 0` guard.
        if (arguments.TryGetValue("_timeout_seconds", out var tVal)
            && tVal is not null and not JsonElement { ValueKind: JsonValueKind.Null }
            && !(ToolArgumentHelper.TryCoerceInt(tVal, out var t) && t > 0))
        {
            return $"Error: Meta argument '_timeout_seconds' value '{ToolArgumentHelper.RenderValue(tVal)}' is not a valid positive integer. The tool was NOT executed.";
        }

        if (arguments.TryGetValue("_background", out var bVal)
            && bVal is not null and not JsonElement { ValueKind: JsonValueKind.Null }
            && !ToolArgumentHelper.TryCoerceBool(bVal, out _))
        {
            return $"Error: Meta argument '_background' value '{ToolArgumentHelper.RenderValue(bVal)}' is not a valid boolean. The tool was NOT executed.";
        }

        return null;
    }
}
