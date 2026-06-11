// -----------------------------------------------------------------------
// <copyright file="ToolArgumentHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

    public static string? GetString(IDictionary<string, object?>? arguments, string key)
    {
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

    public static int? GetNullableInt(IDictionary<string, object?>? arguments, string key)
    {
        if (!TryGetValueFlexible(arguments, key, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetInt32(),
            string s when int.TryParse(s, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } je when int.TryParse(je.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public static double? GetNullableDouble(IDictionary<string, object?>? arguments, string key)
    {
        if (!TryGetValueFlexible(arguments, key, out var value) || value is null)
            return null;

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
            string s when double.TryParse(s, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } je when double.TryParse(je.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public static bool? GetNullableBool(IDictionary<string, object?>? arguments, string key)
    {
        if (!TryGetValueFlexible(arguments, key, out var value) || value is null)
            return null;

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string s when bool.TryParse(s, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } je when bool.TryParse(je.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    // Strict variants distinguish three states the GetNullable* helpers conflate:
    // absent (or JSON null) → null; parseable → value; present-but-invalid →
    // ArgumentException naming the parameter, supplied value, and expected type.
    // Generated ParseArguments uses these so an invalid value rejects the call
    // (surfaced via the pipeline's exception→error-result channel) instead of
    // silently coercing to 0/0.0/false (tool-arg-validation spec).

    public static int? GetIntStrict(IDictionary<string, object?>? arguments, string key)
    {
        if (!TryGetValueFlexible(arguments, key, out var value)
            || value is null or JsonElement { ValueKind: JsonValueKind.Null })
            return null;

        return value switch
        {
            int i => i,
            long l and >= int.MinValue and <= int.MaxValue => (int)l,
            // Non-integral numerics are invalid for an integer parameter — no
            // silent truncation (12.7 must not become 12).
            double d when double.IsInteger(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var parsed) => parsed,
            string s when int.TryParse(s, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } je when int.TryParse(je.GetString(), out var parsed) => parsed,
            _ => throw InvalidValue(key, value, "integer")
        };
    }

    public static double? GetDoubleStrict(IDictionary<string, object?>? arguments, string key)
    {
        if (!TryGetValueFlexible(arguments, key, out var value)
            || value is null or JsonElement { ValueKind: JsonValueKind.Null })
            return null;

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetDouble(out var parsed) => parsed,
            string s when double.TryParse(s, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } je when double.TryParse(je.GetString(), out var parsed) => parsed,
            _ => throw InvalidValue(key, value, "number")
        };
    }

    public static bool? GetBoolStrict(IDictionary<string, object?>? arguments, string key)
    {
        if (!TryGetValueFlexible(arguments, key, out var value)
            || value is null or JsonElement { ValueKind: JsonValueKind.Null })
            return null;

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string s when bool.TryParse(s, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } je when bool.TryParse(je.GetString(), out var parsed) => parsed,
            _ => throw InvalidValue(key, value, "boolean")
        };
    }

    private static ArgumentException InvalidValue(string key, object value, string expectedType)
    {
        var rendered = value switch
        {
            JsonElement je => je.GetRawText(),
            _ => value.ToString() ?? string.Empty
        };

        if (rendered.Length > 100)
            rendered = rendered[..100] + "…";

        return new ArgumentException(
            $"Parameter '{key}' value '{rendered}' is not a valid {expectedType}. The tool was NOT executed.");
    }
}
