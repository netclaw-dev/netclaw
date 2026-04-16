using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

internal static class DoctorJsonConfigReader
{
    /// <summary>
    /// Reads a boolean property from a <see cref="JsonObject"/>. Returns <c>false</c>
    /// if the property is missing, not a boolean, or not <c>true</c>.
    /// </summary>
    public static bool ReadBool(JsonObject obj, string property)
        => obj[property] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// Reads a string array property from a <see cref="JsonObject"/>. Returns an empty
    /// list if the property is missing or not an array.
    /// </summary>
    public static List<string> ReadStringArray(JsonObject obj, string property)
    {
        if (obj[property] is not JsonArray arr)
            return [];

        return arr.Select(v => v?.GetValue<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList();
    }

    public static (JsonObject? Root, DoctorCheckResult? Error) TryReadConfig(NetclawPaths paths)
    {
        if (!File.Exists(paths.NetclawConfigPath))
        {
            return (null, DoctorCheckResult.Warning(
                "Config File",
                $"Config file not found at {paths.NetclawConfigPath}.",
                "Run `netclaw init` to scaffold a baseline config."));
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(paths.NetclawConfigPath)) as JsonObject;
            if (root is null)
            {
                return (null, DoctorCheckResult.Error(
                    "Config File",
                    "netclaw.json root must be a JSON object.",
                    "Wrap config in a top-level JSON object."));
            }

            return (root, null);
        }
        catch (Exception ex)
        {
            return (null, DoctorCheckResult.Error(
                "Config File",
                $"Failed parsing {paths.NetclawConfigPath}: {ex.Message}",
                "Fix malformed JSON in netclaw.json."));
        }
    }
}
