// -----------------------------------------------------------------------
// <copyright file="NotificationsConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for operational notification webhooks.
/// Bound from the "Notifications" section of netclaw.json.
/// </summary>
public sealed class NotificationsConfig
{
    /// <summary>
    /// Webhook targets for operational notifications, keyed by webhook name. Empty = logging only.
    /// The key joins a netclaw.json entry to its secrets.json entry. The old list shape still binds,
    /// with the list index as the key ("0", "1", ...).
    /// </summary>
    public Dictionary<string, WebhookTarget> Webhooks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Minimum interval in seconds between duplicate alerts of the same type
    /// and context key. Prevents flood when an MCP server reconnect-fails in a loop.
    /// </summary>
    public int DeduplicationWindowSeconds { get; set; } = 300;

    /// <summary>
    /// Number of delivery retries per webhook target before giving up.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// HTTP timeout in seconds for each webhook POST attempt.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// A single webhook endpoint that receives operational alerts.
/// </summary>
public sealed class WebhookTarget
{
    /// <summary>
    /// HTTP(S) endpoint to POST alerts to. The URL can carry a credential (a Slack incoming-webhook
    /// URL is one), so it normally lives encrypted in secrets.json. A plain value in netclaw.json
    /// still loads. <see cref="NotificationsConfigValidator"/> rejects an entry with no URL.
    /// </summary>
    public SensitiveString? Url { get; set; }

    /// <summary>
    /// Optional static headers to include with every POST (e.g. Authorization).
    /// Header values are secrets and normally live in secrets.json.
    /// </summary>
    public Dictionary<string, SensitiveString>? Headers { get; set; }

    /// <summary>
    /// Payload format for this webhook target.
    /// </summary>
    public WebhookFormat Format { get; set; } = WebhookFormat.Generic;
}

/// <summary>
/// Supported webhook payload formats.
/// </summary>
public enum WebhookFormat
{
    /// <summary>Default JSON envelope with alertId, type, severity, summary, etc.</summary>
    Generic = 0,

    /// <summary>Slack Block Kit format with required text field and structured blocks.</summary>
    Slack = 1,
}

/// <summary>
/// Infers the appropriate <see cref="WebhookFormat"/> from a webhook URL.
/// </summary>
public static class WebhookFormatDetection
{
    public static WebhookFormat InferFromUrl(string url)
        => url.Contains("hooks.slack.com", StringComparison.OrdinalIgnoreCase)
            ? WebhookFormat.Slack
            : WebhookFormat.Generic;
}
