using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class NotificationConfigDoctorCheck(NetclawPaths paths, IConfiguration configuration) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var notificationsConfig = configuration.GetSection("Notifications")
            .Get<NotificationsConfig>() ?? new NotificationsConfig();

        var validation = NotificationConfigValidator.Validate(notificationsConfig);
        if (!validation.IsValid)
        {
            var message = string.Join(" ", validation.Issues.Select(static issue =>
                $"{issue.FieldPath}: {issue.Message}"));
            var remediation = string.Join(" ", validation.Issues
                .Select(static issue => issue.Remediation)
                .Distinct(StringComparer.Ordinal));

            return Task.FromResult(DoctorCheckResult.Error(
                "Notification Config",
                message,
                remediation));
        }

        JsonObject? root = null;
        if (File.Exists(paths.NetclawConfigPath))
            (root, _) = DoctorJsonConfigReader.TryReadConfig(paths);

        var headerWarnings = GetBaseConfigHeaderWarnings(root);
        var urlWarnings = GetBaseConfigUrlWarnings(root);
        if (headerWarnings.Count > 0 || urlWarnings.Count > 0)
        {
            var warnings = headerWarnings.Concat(urlWarnings).ToList();
            var message = string.Join(" ", warnings.Select(static warning =>
                $"{warning.FieldPath}: {warning.Message}"));

            return Task.FromResult(DoctorCheckResult.Warning(
                "Notification Config",
                message,
                "Move notification webhook URLs and auth-like headers to secrets.json or NETCLAW_ environment variables."));
        }

        if (notificationsConfig.Webhooks.Count == 0)
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                "Notification Config",
                "Notifications are disabled; no webhook targets configured."));
        }

        var targets = notificationsConfig.Webhooks
            .Select((target, index) => NotificationConfigValidator.FormatTargetIdentity(target, index))
            .ToArray();

        return Task.FromResult(DoctorCheckResult.Pass(
            "Notification Config",
            $"Notification config is valid for {notificationsConfig.Webhooks.Count} webhook target(s): {string.Join(", ", targets)}."));
    }

    private static List<NotificationConfigValidationIssue> GetBaseConfigHeaderWarnings(JsonObject? root)
    {
        var warnings = new List<NotificationConfigValidationIssue>();
        if (root is null)
            return warnings;

        if (root["Notifications"] is not JsonObject notificationsObject)
            return warnings;

        if (notificationsObject["Webhooks"] is not JsonArray webhooksArray)
            return warnings;

        for (var i = 0; i < webhooksArray.Count; i++)
        {
            if (webhooksArray[i] is not JsonObject webhookObject)
                continue;

            if (webhookObject["Headers"] is not JsonObject headersObject)
                continue;

            foreach (var pair in headersObject)
            {
                if (!NotificationConfigValidator.IsAuthLikeHeaderName(pair.Key))
                    continue;

                warnings.Add(new NotificationConfigValidationIssue(
                    $"Notifications.Webhooks[{i}].Headers.{pair.Key}",
                    "Auth-like header is defined in base config and should be treated as a secret.",
                    "Move this header value to secrets.json or a NETCLAW_ environment variable."));
            }
        }

        return warnings;
    }

    private static List<NotificationConfigValidationIssue> GetBaseConfigUrlWarnings(JsonObject? root)
    {
        var warnings = new List<NotificationConfigValidationIssue>();
        if (root is null)
            return warnings;

        if (root["Notifications"] is not JsonObject notificationsObject)
            return warnings;

        if (notificationsObject["Webhooks"] is not JsonArray webhooksArray)
            return warnings;

        for (var i = 0; i < webhooksArray.Count; i++)
        {
            if (webhooksArray[i] is not JsonObject webhookObject)
                continue;

            if (webhookObject["Url"] is null)
                continue;

            warnings.Add(new NotificationConfigValidationIssue(
                $"Notifications.Webhooks[{i}].Url",
                "Webhook URL is defined in base config and should be treated as a secret.",
                "Move this webhook URL to secrets.json or a NETCLAW_ environment variable."));
        }

        return warnings;
    }
}
