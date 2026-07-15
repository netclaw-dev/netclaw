// -----------------------------------------------------------------------
// <copyright file="WebhooksConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Configuration for inbound webhook routes.
/// </summary>
public sealed class WebhooksConfig
{
    /// <summary>
    /// Enables inbound webhook endpoint registration.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum webhook execution time in seconds before the daemon marks the
    /// autonomous run failed.
    /// </summary>
    public int ExecutionTimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// A single inbound webhook route.
/// </summary>
public sealed class WebhookRouteConfig
{
    public bool Enabled { get; set; } = true;

    public WebhookVerificationConfig Verification { get; set; } = new();

    public List<string> Events { get; set; } = [];

    public TrustAudience Audience { get; set; } = TrustAudience.Public;

    public string Prompt { get; set; } = string.Empty;

    public string NotifyInstructions { get; set; } = string.Empty;

    public bool DeliveryRequired { get; set; } = true;

    public NotificationTargetConfig? NotificationTarget { get; set; }

    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    public int RateLimitPerMinute { get; set; } = 30;
}

/// <summary>
/// Verification settings for an inbound webhook route.
/// </summary>
public sealed class WebhookVerificationConfig
{
    public WebhookVerifierKind Kind { get; set; } = WebhookVerifierKind.Hmac;

    public WebhookHmacAlgorithm HmacAlgorithm { get; set; } = WebhookHmacAlgorithm.Sha256;

    public SensitiveString? Secret { get; set; }

    public string? SignatureHeaderName { get; set; }

    public string? SignaturePrefix { get; set; }

    public string? SecretHeaderName { get; set; }

    public string? EventHeaderName { get; set; }

    public string? DeliveryIdHeaderName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ToleranceSeconds { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimestampField { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignatureField { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignedPayloadSeparator { get; set; }
}

public enum WebhookVerifierKind
{
    Hmac = 0,
    HeaderSecret = 1,
    HmacTimestamped = 2
}

public enum WebhookHmacAlgorithm
{
    Sha256 = 0,
}

/// <summary>
/// Human-facing notification target for an automated invocation.
/// </summary>
public sealed class NotificationTargetConfig
{
    public NotificationTargetKind Kind { get; set; } = NotificationTargetKind.Slack;

    public string? ChannelId { get; set; }
}

public enum NotificationTargetKind
{
    Slack = 0
}
