using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

internal static class DoctorJsonConfigReader
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

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
