using Netclaw.Configuration;

namespace Netclaw.Daemon.Webhooks;

public static class WebhookPromptBuilder
{
    public static string BuildOverlay(RegisteredWebhookRoute route)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(route.Config.Prompt))
            parts.Add(route.Config.Prompt.Trim());

        var notifyInstructions = string.IsNullOrWhiteSpace(route.Config.NotifyInstructions)
            ? route.BuildDefaultNotifyInstructions()
            : route.Config.NotifyInstructions.Trim();

        if (!string.IsNullOrWhiteSpace(notifyInstructions))
        {
            var notifyHeader = route.Config.NotifyPolicy == NotificationPolicy.Conditional
                ? "Notification instructions (only notify if results warrant it — it is OK to skip notification if there is nothing actionable):"
                : "Notification instructions:";

            parts.Add($"{notifyHeader}\n{notifyInstructions}");
        }

        return string.Join("\n\n", parts);
    }
}
