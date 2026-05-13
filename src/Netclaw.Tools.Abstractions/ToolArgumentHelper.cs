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

    private static string NormalizeKey(string key)
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
}
