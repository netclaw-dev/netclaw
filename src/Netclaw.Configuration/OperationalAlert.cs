// -----------------------------------------------------------------------
// <copyright file="OperationalAlert.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Severity levels for operational alerts.
/// </summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Critical
}

/// <summary>
/// Categories of operational alerts that can be emitted by daemon components.
/// </summary>
public enum AlertType
{
    McpAuthExpired,
    McpServerDisconnected,
    McpServerReconnected,
    ChannelDisconnected,
    ProviderAuthExpired,
    ProviderFailover,
    ProviderUnreachable,
    ReminderExecutionFailed,
    ReminderAutoDisabled,
    ReminderSchemaDropped,
    BackgroundJobSchemaDropped,
    WebhookReceived,
    WebhookRouteInvalid,
    DaemonStarted,
    DaemonStopping,
    DaemonCrashed,
    UpdateAvailable,
}

/// <summary>
/// An operational event that may need human attention.
/// Immutable value object — safe to pass across threads.
/// </summary>
public sealed record OperationalAlert
{
    /// <summary>Unique alert ID for deduplication and correlation.</summary>
    public required string AlertId { get; init; }

    /// <summary>Wire-format alert type string (e.g., "mcp.auth.expired").</summary>
    public required string Type { get; init; }

    /// <summary>Enum category for programmatic use.</summary>
    public required AlertType Category { get; init; }

    /// <summary>Human-readable summary of what happened.</summary>
    public required string Summary { get; init; }

    /// <summary>UTC timestamp when the event occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Severity level.</summary>
    public required AlertSeverity Severity { get; init; }

    /// <summary>
    /// Stable source identifier for deduplication (e.g., MCP server name, channel name).
    /// When set, alerts are deduplicated by Type + Source. When null, deduplicated by Type only.
    /// This is intentionally separate from Context to avoid fragile dictionary iteration order.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Additional context (server name, provider name, error message, etc).
    /// Included in the webhook payload for diagnostic purposes.
    /// </summary>
    public Dictionary<string, string>? Context { get; init; }

    /// <summary>
    /// Deduplication key built from Type and Source.
    /// </summary>
    public string DeduplicationKey => Source is not null ? $"{Type}:{Source}" : Type;

    /// <summary>
    /// Creates an <see cref="OperationalAlert"/> with a generated <see cref="IdGen.AlertId"/>
    /// and current timestamp from the provided <paramref name="timeProvider"/>.
    /// </summary>
    public static OperationalAlert Create(
        TimeProvider timeProvider,
        string type,
        AlertType category,
        string summary,
        AlertSeverity severity,
        string? source = null,
        Dictionary<string, string>? context = null) => new()
    {
        AlertId = IdGen.AlertId(),
        Type = type,
        Category = category,
        Summary = summary,
        Timestamp = timeProvider.GetUtcNow(),
        Severity = severity,
        Source = source,
        Context = context
    };
}
