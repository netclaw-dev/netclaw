using Netclaw.Actors.Protocol;
using ProtoBuf;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Strongly-typed reminder identity.
/// </summary>
[ProtoContract]
public readonly record struct ReminderId(
    [property: ProtoMember(1)] string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// The type of schedule for a reminder.
/// </summary>
[ProtoContract]
public enum ReminderScheduleType
{
    OneShot = 0,
    Interval = 1,
    Cron = 2
}

/// <summary>
/// Describes when and how a reminder fires.
/// </summary>
[ProtoContract]
public sealed class ReminderSchedule
{
    [ProtoMember(1)]
    public ReminderScheduleType Type { get; set; }

    /// <summary>
    /// For OneShot: the absolute fire time.
    /// For Interval/Cron: the first fire time.
    /// Stored as milliseconds since Unix epoch for serialization.
    /// </summary>
    [ProtoMember(2)]
    public long? FireAtMs { get; set; }

    [ProtoIgnore]
    public DateTimeOffset? FireAt
    {
        get => FireAtMs is not null ? DateTimeOffset.FromUnixTimeMilliseconds(FireAtMs.Value) : null;
        set => FireAtMs = value?.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// For Interval schedules: the repeat interval.
    /// Stored as ticks for serialization.
    /// </summary>
    [ProtoMember(3)]
    public long? IntervalTicks { get; set; }

    [ProtoIgnore]
    public TimeSpan? Interval
    {
        get => IntervalTicks is not null ? TimeSpan.FromTicks(IntervalTicks.Value) : null;
        set => IntervalTicks = value?.Ticks;
    }

    /// <summary>
    /// For Cron schedules: the cron expression.
    /// </summary>
    [ProtoMember(4)]
    public string? CronExpression { get; set; }

    /// <summary>
    /// Original schedule string as provided by the user (e.g. "6h", "0 */6 * * *").
    /// </summary>
    [ProtoMember(5)]
    public string? OriginalExpression { get; set; }
}

/// <summary>
/// Payload stored with a reminder and delivered when it fires.
/// This is the <c>message</c> object passed to akka-reminders.
/// </summary>
[ProtoContract]
public sealed class ReminderPayload
{
    [ProtoMember(1)]
    public ReminderId Id { get; set; }

    [ProtoMember(2)]
    public string Name { get; set; } = string.Empty;

    [ProtoMember(3)]
    public string Prompt { get; set; } = string.Empty;

    [ProtoMember(4)]
    public ReminderSchedule Schedule { get; set; } = new();

    /// <summary>
    /// Slack channel to post results to. Null = log-only for autonomous reminders.
    /// </summary>
    [ProtoMember(5)]
    public string? ReportToChannel { get; set; }

    /// <summary>
    /// Slack thread TS for self-targeting reminders. Null = create new thread.
    /// </summary>
    [ProtoMember(6)]
    public string? ReportToThreadTs { get; set; }

    /// <summary>
    /// The session ID that created this reminder (for self-targeting).
    /// </summary>
    [ProtoMember(7)]
    public SessionId? OriginatingSessionId { get; set; }

    [ProtoMember(8)]
    public string CreatedBy { get; set; } = "system";

    [ProtoMember(9)]
    public long CreatedAtMs { get; set; }

    [ProtoIgnore]
    public DateTimeOffset CreatedAt
    {
        get => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtMs);
        set => CreatedAtMs = value.ToUnixTimeMilliseconds();
    }
}

// ── Commands ──

public sealed record ScheduleReminderCommand(ReminderPayload Payload);
public sealed record CancelReminderCommand(ReminderId Id);
public sealed record ListRemindersCommand;
public sealed record GetReminderCommand(ReminderId Id);

// ── Responses ──

public sealed record ReminderScheduledResponse(ReminderId Id, string Name, DateTimeOffset? NextFire);
public sealed record ReminderCancelledResponse(ReminderId Id, bool Found);
public sealed record ReminderListResponse(IReadOnlyList<ReminderInfo> Reminders);
public sealed record GetReminderResponse(ReminderInfo? Reminder);

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
