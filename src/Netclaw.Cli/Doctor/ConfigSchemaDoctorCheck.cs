// -----------------------------------------------------------------------
// <copyright file="ConfigSchemaDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Netclaw.Cli;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class ConfigSchemaDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.NetclawConfigPath))
        {
            // Schema validation applies to netclaw.json specifically. An
            // env-only instance (NETCLAW_ config-binding env vars, no file)
            // is configured — warning "run netclaw init" would misdiagnose a
            // healthy deployment.
            if (DoctorJsonConfigReader.HasEnvironmentConfig())
            {
                return Task.FromResult(DoctorCheckResult.Pass(
                    "Config Schema",
                    "No netclaw.json; NETCLAW_ environment configuration detected — schema validation applies to the file only."));
            }

            return Task.FromResult(DoctorCheckResult.Warning(
                "Config Schema",
                CliConfigPreflight.MissingConfigMessage,
                $"Run `netclaw init` to create {paths.NetclawConfigPath}."));
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
            version = EmbeddedSchemaLoader.CurrentSchemaVersion;
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

        var schemaText = EmbeddedSchemaLoader.LoadConfigSchema(version);
        if (schemaText is null)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Config Schema",
                $"No embedded schema found for configVersion {version}.",
                "This binary may be corrupted or built without embedded schemas. Reinstall Netclaw."));
        }

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(schemaText);
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Config Schema",
                $"Failed parsing embedded schema for v{version}: {ex.Message}"));
        }

        var evaluation = schema.Evaluate(obj, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (!evaluation.IsValid)
        {
            var errorDetails = string.Empty;
            if (evaluation.Details is not null)
            {
                var failures = evaluation.Details
                    .Where(d => !d.IsValid && d.Errors is not null)
                    .Take(5)
                    .Select(d => $"  {d.InstanceLocation}: {string.Join("; ", d.Errors!.Select(e => $"{e.Key}: {e.Value}"))}")
                    .ToList();
                if (failures.Count > 0)
                    errorDetails = " Errors:\n" + string.Join("\n", failures);
            }

            return Task.FromResult(DoctorCheckResult.Error(
                "Config Schema",
                $"Config does not match schema v{version}.{errorDetails}",
                "Run `netclaw doctor --fix --dry-run` to preview auto-repairs, or check configVersion/schema fields in netclaw.json."));
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
}
