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
