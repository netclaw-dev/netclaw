using System.Text.Json.Serialization;
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
    /// For Interval: optional explicit first fire.
    /// For Cron: not used.
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
    /// For Interval schedules: repeat interval.
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
    /// For Cron schedules: cron expression.
    /// </summary>
    [ProtoMember(4)]
    public string? CronExpression { get; set; }

    /// <summary>
    /// Original expression as entered by user (e.g. "6h", "0 */6 * * *").
    /// </summary>
    [ProtoMember(5)]
    public string? OriginalExpression { get; set; }
}

/// <summary>
/// Canonical reminder definition persisted to disk.
/// </summary>
public sealed record ReminderDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required ReminderSchedule Schedule { get; init; }
    public required string Instructions { get; init; }
    public required string NotifyInstructions { get; init; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional session to resume when running this reminder.
    /// Useful for routing responses back to the originating Slack thread.
    /// </summary>
    public string? SessionId { get; init; }

    public string? ReportToChannel { get; init; }
    public string? ReportToThreadTs { get; init; }

    /// <summary>
    /// Deferred shadow field for selecting specialized agent behavior.
    /// Tracked by issue #147.
    /// </summary>
    public string? AgentDefinitionId { get; init; }

    public string CreatedBy { get; init; } = "system";
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }

    [JsonIgnore]
    public DateTimeOffset CreatedAt
    {
        get => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtMs);
        set => CreatedAtMs = value.ToUnixTimeMilliseconds();
    }

    [JsonIgnore]
    public DateTimeOffset UpdatedAt
    {
        get => DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAtMs);
        set => UpdatedAtMs = value.ToUnixTimeMilliseconds();
    }
}

/// <summary>
/// Message persisted inside Akka.Reminders. Intentionally lightweight: pointer to disk definition.
/// </summary>
[ProtoContract]
public sealed class ReminderPayload
{
    [ProtoMember(1)]
    public ReminderId Id { get; set; }
}

public enum ReminderWriteMode
{
    CreateOnly,
    Replace,
    Upsert
}

public enum ReminderSaveError
{
    None,
    Conflict,
    NotFound,
    Validation,
    Internal
}

// ── Commands ──

public sealed record SaveReminderCommand(
    ReminderDefinition Definition,
    ReminderWriteMode WriteMode = ReminderWriteMode.CreateOnly);

/// <summary>
/// Permanently deletes a reminder definition and cancels any active schedule.
/// </summary>
public sealed record CancelReminderCommand(ReminderId Id);
public sealed record DisableReminderCommand(ReminderId Id);
public sealed record EnableReminderCommand(ReminderId Id);
public sealed record ListRemindersCommand(bool IncludeDisabled = true);
public sealed record GetReminderCommand(ReminderId Id);

// ── Responses ──

public sealed record ReminderSavedResponse(
    ReminderId Id,
    string Title,
    bool Success,
    DateTimeOffset? NextFire,
    ReminderSaveError Error = ReminderSaveError.None,
    string? ErrorMessage = null);

public sealed record ReminderCancelledResponse(ReminderId Id, bool Found);

public sealed record ReminderStateResponse(
    ReminderId Id,
    bool Found,
    bool Enabled,
    DateTimeOffset? NextFire = null,
    string? ErrorMessage = null);

public sealed record ReminderListResponse(IReadOnlyList<ReminderInfo> Reminders);
public sealed record GetReminderResponse(ReminderInfo? Reminder);

public sealed record ReminderInfo(
    ReminderId Id,
    string Title,
    string Instructions,
    string NotifyInstructions,
    ReminderSchedule Schedule,
    DateTimeOffset? NextFire,
    bool Enabled,
    string? SessionId,
    string? ReportToChannel,
    string? ReportToThreadTs,
    string? AgentDefinitionId);

// ── Internal messages ──

/// <summary>
/// Sent by <see cref="ReminderExecutionActor"/> to parent when execution completes.
/// </summary>
internal sealed record ReminderExecutionCompleted(
    Guid ExecutionId,
    ReminderId Id,
    bool Success,
    string? ErrorMessage = null);
