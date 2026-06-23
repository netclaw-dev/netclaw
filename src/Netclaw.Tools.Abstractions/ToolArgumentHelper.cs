// -----------------------------------------------------------------------
// <copyright file="ToolArgumentHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;

namespace Netclaw.Tools;

/// <summary>
/// Runtime helpers for extracting typed values from tool argument dictionaries.
/// Called by source-generated <c>ParseArguments</c> methods. Handles both
/// <see cref="JsonElement"/> values (OllamaSharp) and native CLR types (OpenAI, etc.).
/// </summary>
public static class ToolArgumentHelper
{
    private static bool TryGetValueFlexible(IDictionary<string, object?>? arguments, string key, out object? value)
    {
        if (arguments is null)
        {
            value = null;
            return false;
        }

        if (arguments.TryGetValue(key, out value))
            return true;

        // Case-insensitive exact-name fallback
        foreach (var kvp in arguments)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        // Normalized-name fallback: treat ChannelId == channel_id == channel-id
        var normalizedKey = NormalizeKey(key);
        foreach (var kvp in arguments)
        {
            if (string.Equals(NormalizeKey(kvp.Key), normalizedKey, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Deterministic key canonicalization (case + punctuation folding):
    /// <c>ChannelId</c>, <c>channel_id</c>, and <c>channel-id</c> all normalize
    /// to <c>channelid</c>. Shared with <see cref="ToolArgumentValidator"/> so
    /// key recognition mirrors the exact matching that binding performs.
    /// </summary>
    public static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var buffer = new char[key.Length];
        var count = 0;

        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[count++] = ch;
        }

        return count == 0 ? string.Empty : new string(buffer, 0, count);
    }

    // Interchangeable parameter-name groups (normalized form). A tool may
    // declare one member and accept another at binding time — e.g. GetString's
    // Message↔text fallback below, and SendChannelMessageTool binding
    // `text ?? Message`. This is the single definition both binding and
    // ToolArgumentValidator's key recognition consume, so validation never
    // rejects a key that binding would happily accept.
    private static readonly string[][] AliasGroups = [["text", "message"]];

    /// <summary>
    /// The normalized names interchangeable with <paramref name="normalizedName"/>
    /// (excluding itself). Empty when the name has no aliases.
    /// </summary>
    public static IEnumerable<string> NormalizedAliasesFor(string normalizedName)
    {
        foreach (var group in AliasGroups)
        {
            if (Array.IndexOf(group, normalizedName) < 0)
                continue;
            foreach (var member in group)
            {
                if (!string.Equals(member, normalizedName, StringComparison.Ordinal))
                    yield return member;
            }
        }
    }

    public static string? GetString(IDictionary<string, object?>? arguments, string key)
    {
        // Binding-side consumer of the text↔Message alias group above.
        if (string.Equals(key, "Message", StringComparison.OrdinalIgnoreCase)
            && !TryGetValueFlexible(arguments, key, out _)
            && TryGetValueFlexible(arguments, "text", out var textAlias)
            && textAlias is not null)
        {
            return textAlias switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                JsonElement je => je.ToString(),
                _ => textAlias.ToString()
            };
        }

        if (!TryGetValueFlexible(arguments, key, out var value) || value is null)
            return null;

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement je => je.ToString(),
            _ => value.ToString()
        };
    }

    // ── Coercion primitives ──
    // Single source of truth for loose-value → typed coercion, shared by the
    // strict argument binders below AND by ToolCallMeta.ExtractFrom /
    // ToolCallMetaExtractor.ValidateMetaValues so the accept-set cannot drift
    // between binding and validation (the drift would reintroduce the silent
    // intent-drop these guards exist to prevent). All string parsing uses
    // InvariantCulture: tool arguments are a machine protocol, not locale-aware
    // user input, and the daemon does not run with InvariantGlobalization.

    /// <summary>
    /// Coerces a loose argument value to <see cref="int"/>. Integral numerics
    /// written with a decimal point (e.g. <c>300.0</c> — a common LLM emission
    /// shape) are accepted; genuinely fractional values (<c>12.7</c>) are not
    /// (no silent truncation). Returns false for any other value.
    /// </summary>
    public static bool TryCoerceInt(object? value, out int result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l and >= int.MinValue and <= int.MaxValue: result = (int)l; return true;
            case double d when double.IsInteger(d) && d is >= int.MinValue and <= int.MaxValue:
                result = (int)d; return true;
            case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out result):
                return true;
            // JSON integral written with a decimal point: TryGetInt32 rejects
            // "300.0", so fall back to a double check that still bars fractions.
            case JsonElement { ValueKind: JsonValueKind.Number } je
                when je.TryGetDouble(out var jd) && double.IsInteger(jd) && jd is >= int.MinValue and <= int.MaxValue:
                result = (int)jd; return true;
            case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result):
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } je
                when int.TryParse(je.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result):
                return true;
            default: result = 0; return false;
        }
    }

    /// <summary>Coerces a loose argument value to <see cref="double"/>.</summary>
    public static bool TryCoerceDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetDouble(out result):
                return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result):
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } je
                when double.TryParse(je.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result):
                return true;
            default: result = 0; return false;
        }
    }

    /// <summary>Coerces a loose argument value to <see cref="bool"/>.</summary>
    public static bool TryCoerceBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool b: result = b; return true;
            case JsonElement { ValueKind: JsonValueKind.True }: result = true; return true;
            case JsonElement { ValueKind: JsonValueKind.False }: result = false; return true;
            case string s when bool.TryParse(s, out result): return true;
            case JsonElement { ValueKind: JsonValueKind.String } je when bool.TryParse(je.GetString(), out result):
                return true;
            default: result = false; return false;
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> is "absent" for binding purposes:
    /// a missing key (caller's responsibility) or an explicit JSON null.
    /// </summary>
    private static bool IsAbsent(object? value)
        => value is null or JsonElement { ValueKind: JsonValueKind.Null };

    // Strict variants distinguish three states: absent (or JSON null) → null;
    // coercible → value; present-but-invalid → ArgumentException naming the
    // parameter, supplied value, and expected type. Generated ParseArguments
    // uses these so an invalid value rejects the call (surfaced via the
    // pipeline's exception→error-result channel) instead of silently coercing
    // to 0/0.0/false (tool-arg-validation spec).

    public static int? GetIntStrict(IDictionary<string, object?>? arguments, string key)
    {
        if (!TryGetValueFlexible(arguments, key, out var value) || IsAbsent(value))
            return null;

        return TryCoerceInt(value, out var result) ? result : throw InvalidValue(key, value!, "integer");
    }

    public static double? GetDoubleStrict(IDictionary<string, object?>? arguments, string key)
    {
        if (!TryGetValueFlexible(arguments, key, out var value) || IsAbsent(value))
            return null;

        return TryCoerceDouble(value, out var result) ? result : throw InvalidValue(key, value!, "number");
    }

    public static bool? GetBoolStrict(IDictionary<string, object?>? arguments, string key)
    {
        if (!TryGetValueFlexible(arguments, key, out var value) || IsAbsent(value))
            return null;

        return TryCoerceBool(value, out var result) ? result : throw InvalidValue(key, value!, "boolean");
    }

    /// <summary>
    /// Renders a loose argument value for a model-facing error message,
    /// bounded so a pathological multi-KB value cannot flood the result.
    /// Shared so every "invalid value" surface renders identically.
    /// </summary>
    public static string RenderValue(object? value, int maxLength = 100)
    {
        var rendered = value switch
        {
            null => "null",
            JsonElement je => je.GetRawText(),
            _ => value.ToString() ?? string.Empty
        };

        return rendered.Length > maxLength ? rendered[..maxLength] + "…" : rendered;
    }

    private static ArgumentException InvalidValue(string key, object value, string expectedType)
        => new($"Parameter '{key}' value '{RenderValue(value)}' is not a valid {expectedType}. The tool was NOT executed.");
}
