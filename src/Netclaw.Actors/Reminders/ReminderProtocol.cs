using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Strongly-typed reminder identity.
/// </summary>
public readonly record struct ReminderId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// The type of schedule for a reminder.
/// </summary>
public enum ReminderScheduleType
{
    OneShot,
    Interval,
    Cron
}

/// <summary>
/// Describes when and how a reminder fires.
/// </summary>
public sealed record ReminderSchedule
{
    public required ReminderScheduleType Type { get; init; }

    /// <summary>
    /// For OneShot: the absolute fire time.
    /// For Interval/Cron: the first fire time.
    /// </summary>
    public DateTimeOffset? FireAt { get; init; }

    /// <summary>
    /// For Interval schedules: the repeat interval.
    /// </summary>
    public TimeSpan? Interval { get; init; }

    /// <summary>
    /// For Cron schedules: the cron expression.
    /// </summary>
    public string? CronExpression { get; init; }

    /// <summary>
    /// Original schedule string as provided by the user (e.g. "6h", "0 */6 * * *").
    /// </summary>
    public string? OriginalExpression { get; init; }
}

/// <summary>
/// Payload stored with a reminder and delivered when it fires.
/// This is the <c>message</c> object passed to akka-reminders.
/// </summary>
public sealed record ReminderPayload
{
    public required ReminderId Id { get; init; }
    public required string Name { get; init; }
    public required string Prompt { get; init; }
    public required ReminderSchedule Schedule { get; init; }

    /// <summary>
    /// Slack channel to post results to. Null = log-only for autonomous reminders.
    /// </summary>
    public string? ReportToChannel { get; init; }

    /// <summary>
    /// Slack thread TS for self-targeting reminders. Null = create new thread.
    /// </summary>
    public string? ReportToThreadTs { get; init; }

    /// <summary>
    /// The session ID that created this reminder (for self-targeting).
    /// </summary>
    public SessionId? OriginatingSessionId { get; init; }

    public string CreatedBy { get; init; } = "system";
    public DateTimeOffset CreatedAt { get; init; }
}

// ── Commands ──

public sealed record ScheduleReminderCommand(ReminderPayload Payload);
public sealed record CancelReminderCommand(ReminderId Id);
public sealed record ListRemindersCommand;

// ── Responses ──

public sealed record ReminderScheduledResponse(ReminderId Id, string Name, DateTimeOffset? NextFire);
public sealed record ReminderCancelledResponse(ReminderId Id, bool Found);
public sealed record ReminderListResponse(IReadOnlyList<ReminderInfo> Reminders);

public sealed record ReminderInfo(
    ReminderId Id,
    string Name,
    string Prompt,
    ReminderSchedule Schedule,
    DateTimeOffset? NextFire,
    string? ReportToChannel);

// ── Internal messages ──

/// <summary>
/// Sent by <see cref="ReminderExecutionActor"/> to parent when execution completes.
/// </summary>
internal sealed record ReminderExecutionCompleted(ReminderId Id, bool Success, string? ErrorMessage = null);

/// <summary>
/// REST API request body for creating a reminder.
/// </summary>
public sealed record CreateReminderRequest
{
    public required string Name { get; init; }
    public required string Prompt { get; init; }
    public required string ScheduleType { get; init; }
    public required string Schedule { get; init; }
    public string? ReportToChannel { get; init; }
}
