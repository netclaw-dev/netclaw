// -----------------------------------------------------------------------
// <copyright file="ToolArgumentValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Netclaw.Tools;

/// <summary>
/// Validates LLM-supplied argument keys against a native tool's declared
/// surface before execution (tool-arg-validation spec). A key is recognized
/// iff it would actually be consumed downstream: declared parameters match
/// exactly or via <see cref="ToolArgumentHelper.NormalizeKey"/> (mirroring
/// the flexible binding in <see cref="ToolArgumentHelper"/>), while meta keys
/// (<c>_</c>-prefixed) match exactly only (mirroring exact extraction in
/// <c>ToolCallMeta.ExtractFrom</c>). Unrecognized keys reject the call with a
/// "did you mean" suggestion — fuzzy matching generates suggestion text ONLY,
/// never acceptance: the LLM resolves ambiguity by re-issuing explicitly.
/// </summary>
public static class ToolArgumentValidator
{
    private sealed record RecognizedKeys(
        HashSet<string> Exact,
        Dictionary<string, string> NormalizedDeclared,
        string[] MetaKeys,
        string[] ValidNames);

    private static readonly ConcurrentDictionary<Type, RecognizedKeys?> Cache = new();

    /// <summary>
    /// Validates the supplied argument keys for <paramref name="tool"/>.
    /// Returns null when all keys are recognized; otherwise a model-facing
    /// error string (the call must not execute).
    /// </summary>
    public static string? ValidateArgumentKeys(INetclawTool tool, IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return null;

        var recognized = Cache.GetOrAdd(tool.GetType(), _ => BuildRecognizedKeys(tool));
        if (recognized is null)
            return null; // schema exposes no property list — nothing to validate against

        List<(string Key, string? Suggestion)>? unknown = null;
        foreach (var key in arguments.Keys)
        {
            if (recognized.Exact.Contains(key))
                continue;

            // Flexible recognition applies to declared params only: binding
            // consumes case/punctuation variants of declared names, but meta
            // extraction is exact-match, so a near-miss meta key would NOT be
            // consumed and must be rejected here.
            var normalized = ToolArgumentHelper.NormalizeKey(key);
            if (recognized.NormalizedDeclared.ContainsKey(normalized))
                continue;

            unknown ??= [];
            unknown.Add((key, SuggestFor(key, normalized, recognized)));
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
        var meta = new List<string>();
        var names = new List<string>();

        foreach (var prop in props.EnumerateObject())
        {
            var name = prop.Name;
            exact.Add(name);
            names.Add(name);

            if (name.StartsWith('_'))
                meta.Add(name);
            else
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

        return new RecognizedKeys(exact, normalizedDeclared, [.. meta], [.. names]);
    }

    private static string? SuggestFor(string key, string normalizedKey, RecognizedKeys recognized)
    {
        // A key that canonicalizes to a meta key (TimeoutSeconds, timeout_seconds,
        // _timeoutSeconds → _timeout_seconds) is the highest-confidence near-miss.
        foreach (var metaKey in recognized.MetaKeys)
        {
            if (string.Equals(
                    ToolArgumentHelper.NormalizeKey(metaKey), normalizedKey,
                    StringComparison.OrdinalIgnoreCase))
                return metaKey;
        }

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
