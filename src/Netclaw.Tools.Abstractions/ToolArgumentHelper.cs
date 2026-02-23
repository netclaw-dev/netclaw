using System.Text.Json;

namespace Netclaw.Tools;

/// <summary>
/// Runtime helpers for extracting typed values from tool argument dictionaries.
/// Called by source-generated <c>ParseArguments</c> methods. Handles both
/// <see cref="JsonElement"/> values (OllamaSharp) and native CLR types (OpenAI, etc.).
/// </summary>
public static class ToolArgumentHelper
{
    public static string? GetString(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement je => je.ToString(),
            _ => value.ToString()
        };
    }

    public static int? GetNullableInt(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
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

    public static double? GetNullableDouble(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
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

    public static bool? GetNullableBool(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
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
