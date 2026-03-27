using System.Text.Json;
using System.Text.Json.Nodes;
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

        var changed = false;

        if (obj["configVersion"] is null)
        {
            obj["configVersion"] = 1;
            changed = true;
        }

        if (obj["Slack"] is JsonObject slack && ReadBool(slack, "Enabled"))
        {
            var hasAllowedChannels = slack["AllowedChannelIds"] is JsonArray { Count: > 0 };
            var hasDefaultChannel = !string.IsNullOrWhiteSpace(slack["DefaultChannelId"]?.GetValue<string>())
                                    || !string.IsNullOrWhiteSpace(slack["DefaultChannelName"]?.GetValue<string>());

            if (!hasAllowedChannels && !hasDefaultChannel)
            {
                slack["AllowedChannelIds"] = new JsonArray();
                changed = true;
            }
        }

        if (obj["Telemetry"] is JsonObject telemetry && ReadBool(telemetry, "Enabled"))
        {
            telemetry["Otlp"] ??= new JsonObject();
            if (telemetry["Otlp"] is JsonObject otlp
                && string.IsNullOrWhiteSpace(otlp["Endpoint"]?.GetValue<string>()))
            {
                otlp["Endpoint"] = "http://127.0.0.1:4317";
                changed = true;
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
                    changed = true;
                }
            }
        }

        if (changed)
        {
            var normalized = obj.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var replacement = normalized.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                ? normalized
                : normalized + Environment.NewLine;

            fixes.Add(new DoctorFileFix(
                FilePath: paths.NetclawConfigPath,
                Description: "Apply safe configuration autofixes (schema version, ACL defaults, telemetry endpoint, webhook format).",
                OriginalText: original,
                UpdatedText: replacement));
        }

        return Task.FromResult(new DoctorFixPlan(fixes));
    }

    public async Task ApplyAsync(DoctorFixPlan plan, CancellationToken cancellationToken = default)
    {
        foreach (var fix in plan.Fixes)
            await File.WriteAllTextAsync(fix.FilePath, fix.UpdatedText, cancellationToken);
    }

    private static bool ReadBool(JsonObject obj, string property)
        => obj[property] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
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
