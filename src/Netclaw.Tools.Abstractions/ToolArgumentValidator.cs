// -----------------------------------------------------------------------
// <copyright file="ToolArgumentValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Netclaw.Tools;

/// <summary>
/// Validates LLM-supplied argument keys against a tool's declared surface before
/// execution (tool-arg-validation spec). A key is recognized iff it would
/// actually be consumed downstream: declared parameters match exactly or via
/// <see cref="ToolArgumentHelper.NormalizeKey"/> (mirroring the flexible binding
/// in <see cref="ToolArgumentHelper"/>), and meta names (<c>TimeoutSeconds</c> →
/// <c>_timeout_seconds</c>) resolve via <see cref="ResolveMetaField(INetclawTool, string)"/>,
/// which yields to a declared parameter of the same name. Unrecognized keys reject
/// the call with a "did you mean" suggestion — fuzzy matching generates suggestion
/// text ONLY, never acceptance: the LLM resolves ambiguity by re-issuing explicitly.
/// </summary>
public static class ToolArgumentValidator
{
    private sealed record RecognizedKeys(
        HashSet<string> Exact,
        Dictionary<string, string> NormalizedDeclared,
        string[] ValidNames);

    // Keyed by tool INSTANCE, not Type: MCP tools all share the McpToolAdapter
    // type but each carries a distinct server-defined schema, so a Type cache
    // would conflate their declared parameters. ConditionalWeakTable lets the
    // entries be collected with their tools.
    private sealed class RecognizedHolder
    {
        public RecognizedKeys? Value;
    }

    private static readonly ConditionalWeakTable<INetclawTool, RecognizedHolder> Cache = new();

    private static RecognizedKeys? GetRecognized(INetclawTool tool)
        => Cache.GetValue(tool, static t => new RecognizedHolder { Value = BuildRecognizedKeys(t) }).Value;

    /// <summary>
    /// Resolves <paramref name="key"/> to the canonical meta field it addresses
    /// (<c>_rationale</c>/<c>_timeout_seconds</c>/<c>_background</c>), or null when
    /// it is not meta. Schema-aware: if <paramref name="tool"/> declares a real
    /// parameter that <paramref name="key"/> binds to, the parameter wins and this
    /// returns null — so a third-party MCP tool's own <c>timeout</c> argument is
    /// forwarded to the server, never hijacked as a Netclaw meta hint. The exact
    /// canonical names always resolve as meta (they are the injected underscore
    /// forms no real parameter shares).
    /// </summary>
    public static string? ResolveMetaField(INetclawTool tool, string key)
    {
        if (ToolArgumentHelper.ResolveMetaField(key) is not { } canonical)
            return null;

        // Exact canonical meta keys (_timeout_seconds) are always meta.
        if (Array.IndexOf(ToolCallMeta.MetaFieldNames, key) >= 0)
            return canonical;

        // Otherwise a declared (non-meta) parameter of this tool wins.
        var recognized = GetRecognized(tool);
        if (recognized is not null
            && recognized.NormalizedDeclared.ContainsKey(ToolArgumentHelper.NormalizeKey(key)))
            return null;

        return canonical;
    }

    /// <summary>
    /// Validates the supplied argument keys for <paramref name="tool"/>.
    /// Returns null when all keys are recognized; otherwise a model-facing
    /// error string (the call must not execute).
    /// </summary>
    public static string? ValidateArgumentKeys(INetclawTool tool, IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return null;

        var recognized = GetRecognized(tool);
        if (recognized is null)
            return null; // schema exposes no property list — nothing to validate against

        // Classify each key as declared param, near-miss meta, or unknown. Declared
        // params are matched first; only then is a key tested as a near-miss meta
        // name, so the tool-agnostic resolve is safe (a declared param can never be
        // mistaken for meta here). Meta-value validity and ambiguous double-spellings
        // are enforced upstream in ToolCallMetaExtractor.ValidateMetaValues (which
        // runs for every tool, native and MCP — this method is native-only).
        List<(string Key, string? Suggestion)>? unknown = null;
        foreach (var key in arguments.Keys)
        {
            if (recognized.Exact.Contains(key))
                continue; // exact schema property (declared param or injected meta key)

            // NormalizeKey once, reused for the declared-param and meta checks.
            var normalized = ToolArgumentHelper.NormalizeKey(key);

            // Flexible recognition for declared params: binding consumes
            // case/punctuation variants of declared names.
            if (recognized.NormalizedDeclared.ContainsKey(normalized))
                continue;

            // Not a declared param — recognize ChatGPT-style near-miss meta names
            // (TimeoutSeconds, Rationale, bare Timeout) so they are not flagged
            // unknown; extraction consumes them the same way.
            if (ToolArgumentHelper.ResolveMetaField(key) is not null)
                continue;

            unknown ??= [];
            unknown.Add((key, SuggestFor(normalized, recognized)));
        }

        if (unknown is null)
            return null;

        var sb = new StringBuilder("Error: ");
        foreach (var (key, suggestion) in unknown)
        {
            sb.Append($"Unrecognized argument '{key}' for tool '{tool.Name}'.");
            if (suggestion is not null)
                sb.Append($" Did you mean '{suggestion}'?");
            sb.Append(' ');
        }

        sb.Append("The tool was NOT executed. Valid arguments: ");
        sb.Append(string.Join(", ", recognized.ValidNames));
        sb.Append('.');
        return sb.ToString();
    }

    private static RecognizedKeys? BuildRecognizedKeys(INetclawTool tool)
    {
        JsonElement props;
        try
        {
            if (!tool.ParameterSchema.TryGetProperty("properties", out props)
                || props.ValueKind != JsonValueKind.Object)
                return null;
        }
        catch (InvalidOperationException)
        {
            return null; // schema is not an object (defensive; native generated schemas always are)
        }

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var normalizedDeclared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        foreach (var prop in props.EnumerateObject())
        {
            var name = prop.Name;
            exact.Add(name);
            names.Add(name);

            // Meta keys (_-prefixed) are recognized via ResolveMetaField, not the
            // declared-param table, so they are not tracked here.
            if (!name.StartsWith('_'))
                normalizedDeclared[ToolArgumentHelper.NormalizeKey(name)] = name;
        }

        // A key binding would consume via an interchangeable alias (text↔message)
        // must be recognized too, or validation rejects calls binding accepts.
        // The alias groups live in ToolArgumentHelper so binding and validation
        // share one definition (bidirectional: declaring either member accepts
        // the other).
        foreach (var declared in normalizedDeclared.Keys.ToArray())
        {
            foreach (var alias in ToolArgumentHelper.NormalizedAliasesFor(declared))
                normalizedDeclared.TryAdd(alias, normalizedDeclared[declared]);
        }

        return new RecognizedKeys(exact, normalizedDeclared, [.. names]);
    }

    private static string? SuggestFor(string normalizedKey, RecognizedKeys recognized)
    {
        // Near-miss meta keys never reach here — ValidateArgumentKeys recognizes
        // anything ResolveMetaField maps to a meta field before falling through to
        // the unknown-key suggestion path. Only declared-param typos remain.
        string? best = null;
        var bestDistance = 3; // suggest only within edit distance 2
        foreach (var name in recognized.ValidNames)
        {
            var distance = BoundedLevenshtein(
                normalizedKey,
                ToolArgumentHelper.NormalizeKey(name),
                maxDistance: 2);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = name;
            }
        }

        return best;
    }

    /// <summary>
    /// Levenshtein distance with early exit once <paramref name="maxDistance"/>
    /// is exceeded (returns <c>maxDistance + 1</c> in that case).
    /// </summary>
    private static int BoundedLevenshtein(string a, string b, int maxDistance)
    {
        if (Math.Abs(a.Length - b.Length) > maxDistance)
            return maxDistance + 1;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > maxDistance)
                return maxDistance + 1;

            (previous, current) = (current, previous);
        }

        return previous[b.Length] <= maxDistance ? previous[b.Length] : maxDistance + 1;
    }
}
