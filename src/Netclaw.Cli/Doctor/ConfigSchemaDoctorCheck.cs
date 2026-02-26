using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class ConfigSchemaDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.NetclawConfigPath))
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Config Schema",
                $"Config file not found at {paths.NetclawConfigPath}.",
                "Run `netclaw init` to scaffold a baseline config."));
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(paths.NetclawConfigPath));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Config Schema",
                $"Failed parsing {paths.NetclawConfigPath}: {ex.Message}",
                "Fix malformed JSON in netclaw.json."));
        }

        if (root is not JsonObject obj)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Config Schema",
                "netclaw.json root must be a JSON object.",
                "Wrap config in a top-level JSON object."));
        }

        var syntheticVersion = false;
        int version;
        if (obj["configVersion"] is JsonValue versionValue && versionValue.TryGetValue<int>(out var parsedVersion))
        {
            version = parsedVersion;
        }
        else if (obj["configVersion"] is null)
        {
            version = 1;
            obj["configVersion"] = version;
            syntheticVersion = true;
        }
        else
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Config Schema",
                "configVersion must be an integer.",
                "Set `configVersion` to a supported integer value, e.g. 1."));
        }

        var schemaPath = ResolveSchemaPath(version);
        if (!File.Exists(schemaPath))
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Config Schema",
                $"No schema found for configVersion {version}.",
                $"Install/update Netclaw schema file: {schemaPath}"));
        }

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Config Schema",
                $"Failed loading schema {schemaPath}: {ex.Message}"));
        }

        var evaluation = schema.Evaluate(obj, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (!evaluation.IsValid)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Config Schema",
                "Config does not match schema.",
                "Run `netclaw config validate` (planned) or check configVersion/schema fields in netclaw.json."));
        }

        if (syntheticVersion)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Config Schema",
                "Config validated against schema v1, but configVersion is missing.",
                "Add `\"configVersion\": 1` to netclaw.json."));
        }

        return Task.FromResult(DoctorCheckResult.Pass(
            "Config Schema",
            $"Config matches schema v{version}."));
    }

    private static string ResolveSchemaPath(int version)
    {
        var fileName = $"netclaw-config.v{version}.schema.json";
        var runtimePath = Path.Combine(AppContext.BaseDirectory, "Schemas", fileName);
        if (File.Exists(runtimePath))
            return runtimePath;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Netclaw.Cli", "Schemas", fileName);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return runtimePath;
    }
}
