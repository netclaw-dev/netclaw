namespace Netclaw.Configuration;

/// <summary>
/// Categories of operational alerts that can be emitted by daemon components.
/// </summary>
public enum AlertType
{
    McpAuthExpired,
    McpServerDisconnected,
    ChannelDisconnected,
    ProviderAuthExpired,
    ProviderFailover,
    ProviderUnreachable,
    ReminderExecutionFailed,
    ReminderAutoDisabled,
    DaemonStarted,
    DaemonStopping,
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

    /// <summary>Severity level: "info", "warning", "critical".</summary>
    public required string Severity { get; init; }

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
}
