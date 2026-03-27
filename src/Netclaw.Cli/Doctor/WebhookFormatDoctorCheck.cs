using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class WebhookFormatDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "Webhook Format";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, readError) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (readError is not null)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "Skipped (config missing or invalid)."));

        if (root!["Notifications"] is not JsonObject notifications
            || notifications["Webhooks"] is not JsonArray webhooks
            || webhooks.Count == 0)
        {
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "No webhooks configured."));
        }

        var mismatched = new List<string>();
        foreach (var item in webhooks)
        {
            if (item is not JsonObject wh)
                continue;

            var url = wh["Url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var inferred = WebhookFormatDetection.InferFromUrl(url);
            if (inferred == WebhookFormat.Generic)
                continue; // nothing to warn about for non-Slack URLs

            var formatStr = wh["Format"]?.GetValue<string>();
            if (formatStr is null || formatStr.Equals(nameof(WebhookFormat.Generic), StringComparison.OrdinalIgnoreCase))
            {
                mismatched.Add(MaskWebhookUrl(url));
            }
        }

        if (mismatched.Count == 0)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "All webhook formats are correct."));

        var urls = string.Join(", ", mismatched);
        return Task.FromResult(DoctorCheckResult.Warning(
            CheckName,
            $"Slack webhook URL(s) using Generic format (Slack rejects Generic payloads): {urls}",
            $"Set \"Format\": \"{nameof(WebhookFormat.Slack)}\" on these webhook targets in netclaw.json, or run `netclaw doctor --fix`."));
    }

    private static string MaskWebhookUrl(string url)
    {
        var idx = url.IndexOf("/services/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? url[..(idx + "/services/".Length)] + "***" : url;
    }
}
