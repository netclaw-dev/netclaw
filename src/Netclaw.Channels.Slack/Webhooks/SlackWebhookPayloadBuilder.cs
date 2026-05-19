// -----------------------------------------------------------------------
// <copyright file="SlackWebhookPayloadBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Channels.Slack.Webhooks;

/// <summary>
/// Builds Slack Block Kit payloads for incoming webhook delivery.
/// No SlackNet dependency — uses plain anonymous objects serialized by System.Text.Json.
/// </summary>
public static class SlackWebhookPayloadBuilder
{
    private static readonly string Hostname = Environment.MachineName;
    /// <summary>
    /// Build a Slack-compatible webhook payload with a required <c>text</c> fallback
    /// and a <c>blocks</c> array for rich formatting. <paramref name="identity"/> is
    /// the emitting netclaw instance — surfaced so alerts from multiple instances
    /// in a shared channel can be told apart.
    /// </summary>
    public static object Build(OperationalAlert alert, ServiceIdentity identity)
    {
        var emoji = SeverityEmoji(alert.Severity);
        var blocks = new List<object>
        {
            // Header: emoji + alert type
            new
            {
                type = "header",
                text = new { type = "plain_text", text = $"{emoji} {alert.Type}", emoji = true }
            },
            // Summary section
            new
            {
                type = "section",
                text = new { type = "mrkdwn", text = alert.Summary }
            },
            new
            {
                type = "section",
                fields = BuildFields(alert, identity)
            },
        };

        // Context block: service identity footer plus alert-specific context.
        var elements = new List<object>();
        if (identity.Namespace is not null)
            elements.Add(new { type = "mrkdwn", text = $"*namespace:* {identity.Namespace}" });
        if (identity.InstanceId is not null)
            elements.Add(new { type = "mrkdwn", text = $"*instance:* {identity.InstanceId}" });
        elements.Add(new { type = "mrkdwn", text = $"*version:* {identity.Version}" });
        if (alert.Context is { Count: > 0 })
        {
            elements.AddRange(alert.Context
                .Select(kv => (object)new { type = "mrkdwn", text = $"*{kv.Key}:* {kv.Value}" }));
        }
        blocks.Add(new { type = "context", elements });

        return new
        {
            text = $"{emoji} [{alert.Severity}] {alert.Type}: {alert.Summary}",
            blocks,
        };
    }

    private static List<object> BuildFields(OperationalAlert alert, ServiceIdentity identity)
    {
        var fields = new List<object>
        {
            new { type = "mrkdwn", text = $"*Severity:*\n{alert.Severity}" },
            new { type = "mrkdwn", text = $"*Type:*\n{alert.Type}" },
            new { type = "mrkdwn", text = $"*Timestamp:*\n{alert.Timestamp:u}" },
            new { type = "mrkdwn", text = $"*Service:*\n{identity.Name}" },
            new { type = "mrkdwn", text = $"*Hostname:*\n{Hostname}" },
        };

        if (alert.Source is not null)
            fields.Add(new { type = "mrkdwn", text = $"*Source:*\n{alert.Source}" });

        return fields;
    }

    private static string SeverityEmoji(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => ":red_circle:",
        AlertSeverity.Warning => ":warning:",
        AlertSeverity.Info => ":information_source:",
        _ => ":grey_question:",
    };
}
