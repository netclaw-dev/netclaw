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
    /// and a <c>blocks</c> array for rich formatting.
    /// </summary>
    public static object Build(OperationalAlert alert)
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
            // Fields: severity, timestamp, hostname, source
            new
            {
                type = "section",
                fields = BuildFields(alert)
            },
        };

        // Context block for additional key-value pairs
        if (alert.Context is { Count: > 0 })
        {
            var elements = alert.Context
                .Select(kv => (object)new { type = "mrkdwn", text = $"*{kv.Key}:* {kv.Value}" })
                .ToList();

            blocks.Add(new { type = "context", elements });
        }

        return new
        {
            text = $"{emoji} [{alert.Severity}] {alert.Type}: {alert.Summary}",
            blocks,
        };
    }

    private static List<object> BuildFields(OperationalAlert alert)
    {
        var fields = new List<object>
        {
            new { type = "mrkdwn", text = $"*Severity:*\n{alert.Severity}" },
            new { type = "mrkdwn", text = $"*Type:*\n{alert.Type}" },
            new { type = "mrkdwn", text = $"*Timestamp:*\n{alert.Timestamp:u}" },
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
