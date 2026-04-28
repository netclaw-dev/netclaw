// -----------------------------------------------------------------------
// <copyright file="WebhookPromptBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
            var notifyHeader = route.Config.DeliveryRequired
                ? "Notification instructions:"
                : "Notification instructions (only notify if results warrant it — it is OK to skip notification if there is nothing actionable):";

            parts.Add($"{notifyHeader}\n{notifyInstructions}");
        }

        return string.Join("\n\n", parts);
    }
}
