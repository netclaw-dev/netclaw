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
    /// <param name="resolveMeta">
    /// Maps a key to its canonical meta field (schema-aware for the executor,
    /// exact for persistence). Defaults to exact. See
    /// <see cref="ToolCallMeta.ExtractFrom"/>.
    /// </param>
    public static (ToolCallMeta? Meta, FunctionCallContent CleanedToolCall) Extract(
        FunctionCallContent tc, Func<string, string?>? resolveMeta = null)
    {
        var (meta, cleanArgs) = ToolCallMeta.ExtractFrom(tc.Arguments, resolveMeta);
        if (meta is null)
            return (null, tc);

        var cleanedTc = new FunctionCallContent(tc.CallId, tc.Name, cleanArgs);
        return (meta, cleanedTc);
    }

    /// <summary>
    /// Rejects an unusable meta surface before dispatch: two distinct keys that map
    /// to the same meta field (ambiguous), or a present-but-invalid value. Returns
    /// null when the meta surface is valid; otherwise a model-facing error (the call
    /// must not execute — the agent expressed execution semantics we cannot honor,
    /// so we do not run on defaults instead). Runs for EVERY tool via
    /// <c>DispatchingToolExecutor.ValidateArguments</c>, so it — not the native-only
    /// <see cref="ToolArgumentValidator.ValidateArgumentKeys"/> — is what enforces
    /// the no-silent-discard invariant on the MCP path too. Key resolution mirrors
    /// <see cref="ToolCallMeta.ExtractFrom"/> (the same <paramref name="resolveMeta"/>),
    /// so a near-miss that extraction would consume is the same one checked here, and
    /// errors name the model's own key spelling.
    /// </summary>
    public static string? ValidateMetaValues(
        IDictionary<string, object?>? arguments, Func<string, string?>? resolveMeta = null)
    {
        if (arguments is null || arguments.Count == 0)
            return null;

        resolveMeta ??= ToolCallMeta.ResolveExactMetaField;

        // One pass. Ambiguity (two distinct keys -> one meta field) is reported the
        // moment the second key is seen, ahead of any value error, so the model is
        // told to drop the duplicate rather than fix a value it must remove anyway.
        // Validity is defined as "the shared coercion accepts it" — the same
        // TryCoerce* ToolCallMeta.ExtractFrom binds through — and a timeout must be
        // positive, matching ExtractFrom's `> 0` guard.
        Dictionary<string, string>? seen = null;
        string? valueError = null;
        foreach (var kvp in arguments)
        {
            var canonical = resolveMeta(kvp.Key);
            if (canonical is null)
                continue;

            seen ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (seen.TryGetValue(canonical, out var firstKey)
                && !string.Equals(firstKey, kvp.Key, StringComparison.Ordinal))
            {
                return $"Error: Arguments '{firstKey}' and '{kvp.Key}' both map to the meta field '{canonical}'. Supply only one. The tool was NOT executed.";
            }

            seen[canonical] = kvp.Key;

            if (valueError is not null
                || kvp.Value is null or JsonElement { ValueKind: JsonValueKind.Null })
                continue;

            valueError = canonical switch
            {
                "_timeout_seconds" when !(ToolArgumentHelper.TryCoerceInt(kvp.Value, out var t) && t > 0)
                    => $"Error: Meta argument '{kvp.Key}' value '{ToolArgumentHelper.RenderValue(kvp.Value)}' is not a valid positive integer. The tool was NOT executed.",
                "_background" when !ToolArgumentHelper.TryCoerceBool(kvp.Value, out _)
                    => $"Error: Meta argument '{kvp.Key}' value '{ToolArgumentHelper.RenderValue(kvp.Value)}' is not a valid boolean. The tool was NOT executed.",
                _ => null
            };
        }

        return valueError;
    }
}
