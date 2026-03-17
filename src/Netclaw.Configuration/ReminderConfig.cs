namespace Netclaw.Configuration;

/// <summary>
/// Configuration for the reminder system. Bound from the "Reminders" section of netclaw.json.
/// </summary>
public sealed record ReminderConfig
{
    /// <summary>
    /// Maximum number of reminder executions running concurrently.
    /// </summary>
    public int MaxConcurrentExecutions { get; init; } = 3;

    /// <summary>
    /// Timeout in seconds for a single reminder execution before it is killed.
    /// </summary>
    public int ExecutionTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Number of consecutive failures before a reminder is auto-cancelled.
    /// </summary>
    public int FailurePauseThreshold { get; init; } = 5;

    /// <summary>
    /// Minimum allowed interval in seconds for recurring reminders.
    /// Prevents accidental tight loops.
    /// </summary>
    public int MinIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Maximum number of execution history records retained per reminder.
    /// When exceeded, the oldest records are trimmed.
    /// </summary>
    public int HistoryMaxRecords { get; init; } = 500;
}
