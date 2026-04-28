// -----------------------------------------------------------------------
// <copyright file="DoctorFixService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Json.Schema;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class DoctorFixService(NetclawPaths paths)
{
    public Task<DoctorFixPlan> BuildPlanAsync(CancellationToken cancellationToken = default)
    {
        var fixes = new List<DoctorFileFix>();

        if (!File.Exists(paths.NetclawConfigPath))
            return Task.FromResult(new DoctorFixPlan(fixes));

        string original;
        JsonObject? obj;
        try
        {
            original = File.ReadAllText(paths.NetclawConfigPath);
            obj = JsonNode.Parse(original) as JsonObject;
        }
        catch
        {
            return Task.FromResult(new DoctorFixPlan(fixes));
        }

        if (obj is null)
            return Task.FromResult(new DoctorFixPlan(fixes));

        var appliedFixes = new List<string>();

        // --- Manual fixes (not derivable from schema alone) ---

        if (obj["configVersion"] is null)
        {
            obj["configVersion"] = 1;
            appliedFixes.Add("configVersion");
        }

        if (obj["Slack"] is JsonObject slack && DoctorJsonConfigReader.ReadBool(slack, "Enabled"))
        {
            var hasAllowedChannels = slack["AllowedChannelIds"] is JsonArray { Count: > 0 };
            var hasDefaultChannel = !string.IsNullOrWhiteSpace(slack["DefaultChannelId"]?.GetValue<string>())
                                    || !string.IsNullOrWhiteSpace(slack["DefaultChannelName"]?.GetValue<string>());

            if (!hasAllowedChannels && !hasDefaultChannel)
            {
                slack["AllowedChannelIds"] = new JsonArray();
                appliedFixes.Add("Slack ACL defaults");
            }
        }

        if (obj["Telemetry"] is JsonObject telemetry && DoctorJsonConfigReader.ReadBool(telemetry, "Enabled"))
        {
            telemetry["Otlp"] ??= new JsonObject();
            if (telemetry["Otlp"] is JsonObject otlp
                && string.IsNullOrWhiteSpace(otlp["Endpoint"]?.GetValue<string>()))
            {
                otlp["Endpoint"] = "http://127.0.0.1:4317";
                appliedFixes.Add("telemetry endpoint");
            }
        }

        // Webhook format auto-detection
        if (obj["Notifications"] is JsonObject notif
            && notif["Webhooks"] is JsonArray webhooksArr)
        {
            foreach (var item in webhooksArr)
            {
                if (item is not JsonObject wh)
                    continue;

                var url = wh["Url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                if (WebhookFormatDetection.InferFromUrl(url) != WebhookFormat.Slack)
                    continue;

                var existing = wh["Format"]?.GetValue<string>();
                if (existing is null || existing.Equals(nameof(WebhookFormat.Generic), StringComparison.OrdinalIgnoreCase))
                {
                    wh["Format"] = nameof(WebhookFormat.Slack);
                    appliedFixes.Add("webhook format");
                }
            }
        }

        // --- Schema-driven fixes ---
        TryApplySchemaFixes(obj, appliedFixes);

        if (appliedFixes.Count > 0)
        {
            var normalized = obj.ToJsonString(JsonDefaults.Indented);

            var replacement = normalized.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                ? normalized
                : normalized + Environment.NewLine;

            fixes.Add(new DoctorFileFix(
                FilePath: paths.NetclawConfigPath,
                Description: $"Apply safe configuration autofixes ({string.Join(", ", appliedFixes)}).",
                OriginalText: original,
                UpdatedText: replacement));
        }

        return Task.FromResult(new DoctorFixPlan(fixes));
    }

    private void TryApplySchemaFixes(JsonObject config, List<string> appliedFixes)
    {
        // Resolve schema version (default to 1 if not present or invalid)
        var version = 1;
        if (config["configVersion"] is JsonValue versionValue
            && versionValue.TryGetValue<int>(out var parsedVersion))
        {
            version = parsedVersion;
        }

        var schemaPath = ConfigSchemaDoctorCheck.ResolveSchemaPath(version);
        if (!File.Exists(schemaPath))
            return;

        JsonSchema schema;
        JsonObject? schemaJson;
        try
        {
            var schemaText = File.ReadAllText(schemaPath);
            schema = JsonSchema.FromText(schemaText);
            schemaJson = JsonNode.Parse(schemaText) as JsonObject;
        }
        catch
        {
            return;
        }

        if (schemaJson is null)
            return;

        if (SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var schemaFixes))
            appliedFixes.AddRange(schemaFixes);
    }

    public async Task ApplyAsync(DoctorFixPlan plan, CancellationToken cancellationToken = default)
    {
        foreach (var fix in plan.Fixes)
            await File.WriteAllTextAsync(fix.FilePath, fix.UpdatedText, cancellationToken);
    }

}

public sealed record DoctorFixPlan(IReadOnlyList<DoctorFileFix> Fixes)
{
    public bool HasChanges => Fixes.Count > 0;
}

public sealed record DoctorFileFix(
    string FilePath,
    string Description,
    string OriginalText,
    string UpdatedText);
