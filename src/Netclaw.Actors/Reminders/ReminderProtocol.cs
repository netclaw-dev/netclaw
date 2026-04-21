using System.Text.Json.Serialization;
using ProtoBuf;
using Netclaw.Configuration;

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
/// How reminder results are delivered.
/// </summary>
[ProtoContract]
public enum DeliveryKind
{
    /// <summary>
    /// Re-enter the originating session. The reminder turn is delivered
    /// through the session's existing channel pipeline.
    /// </summary>
    CurrentSession = 0,

    /// <summary>
    /// Post to a specific channel/user via the transport's notification tool.
    /// Requires <see cref="ReminderDelivery.Transport"/> and
    /// <see cref="ReminderDelivery.Address"/> to be set.
    /// </summary>
    Channel = 1,

    /// <summary>
    /// Silent execution. Task runs and history is recorded, but no
    /// external notification is sent.
    /// </summary>
    None = 2
}

/// <summary>
/// Structured delivery target for a reminder.
/// </summary>
[ProtoContract]
public sealed class ReminderDelivery
{
    /// <summary>
    /// How results are delivered.
    /// </summary>
    [ProtoMember(1)]
    public DeliveryKind Kind { get; set; }

    /// <summary>
    /// Transport identifier for Channel delivery (e.g., "slack").
    /// Null for CurrentSession and None.
    /// </summary>
    [ProtoMember(2)]
    public string? Transport { get; set; }

    /// <summary>
    /// Canonical target address for Channel delivery (e.g., "C0123ABC").
    /// Resolved and validated at set time. Null for CurrentSession and None.
    /// </summary>
    [ProtoMember(3)]
    public string? Address { get; set; }

    /// <summary>
    /// Session ID for CurrentSession delivery. Null for Channel and None.
    /// </summary>
    [ProtoMember(4)]
    public string? SessionId { get; set; }

    /// <summary>
    /// Channel type of the originating session for CurrentSession delivery.
    /// Used to route DeliverTrustedSessionTurn to the correct gateway.
    /// Null for Channel and None.
    /// </summary>
    [ProtoMember(5)]
    public Channels.ChannelType? OriginChannelType { get; set; }

    /// <summary>
    /// Gets the notification tool name for Channel delivery based on the transport.
    /// Returns null for non-Channel delivery kinds or unknown transports.
    /// </summary>
    public string? GetNotificationToolName() => Kind == DeliveryKind.Channel
        ? Transport?.ToLowerInvariant() switch
        {
            "slack" => "send_slack_message",
            "discord" => "send_discord_message",
            _ => null
        }
        : null;
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

    /// <summary>
    /// Structured delivery target. Determines execution mode and routing.
    /// </summary>
    public required ReminderDelivery Delivery { get; init; }

    /// <summary>
    /// When true, a missed delivery fails the execution and emits
    /// <see cref="OperationalAlert.ReminderExecutionFailed"/>. For
    /// <see cref="DeliveryKind.CurrentSession"/>, this gates envelope ack
    /// on the <see cref="ReminderDeliveryObserved"/> signal. For
    /// <see cref="DeliveryKind.Channel"/>, this gates success on the
    /// notification tool being called. Ignored for <see cref="DeliveryKind.None"/>.
    /// </summary>
    public bool DeliveryRequired { get; init; } = true;

    /// <summary>
    /// Optional guidance for what to include in the delivery to the user.
    /// Content guidance only — never affects routing.
    /// </summary>
    public string? DeliveryInstructions { get; init; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Deferred shadow field for selecting specialized agent behavior.
    /// Tracked by issue #147.
    /// </summary>
    public string? AgentDefinitionId { get; init; }

    /// <summary>
    /// Persisted execution audience for this reminder.
    /// Conversational and tool-created reminders inherit the creating
    /// session/channel audience when omitted at mint time. Reminder save paths
    /// fail closed if they cannot resolve or authorize this audience.
    /// </summary>
    public TrustAudience? Audience { get; init; }

    /// <summary>
    /// Persisted execution boundary for this reminder.
    /// For Mode B reminders this should mirror the creating session's
    /// effective trust boundary so reminder re-entry does not widen scope.
    /// </summary>
    public string? Boundary { get; init; }

    public string CreatedBy { get; init; } = "system";
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }

    /// <summary>
    /// Optional expiration for recurring reminders. When set, the reminder
    /// auto-disables on next fire after this time without executing.
    /// Null means no expiration (default for backwards compatibility).
    /// </summary>
    public long? ExpiresAtMs { get; set; }

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

    [JsonIgnore]
    public DateTimeOffset? ExpiresAt
    {
        get => ExpiresAtMs is not null ? DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtMs.Value) : null;
        set => ExpiresAtMs = value?.ToUnixTimeMilliseconds();
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
    ReminderWriteMode WriteMode = ReminderWriteMode.CreateOnly,
    ReminderAudienceAuthorizationContext? Authorization = null);

public sealed record ReminderAudienceAuthorizationContext(
    TrustAudience? SourceAudience,
    string? SourceDescription = null);

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
    ReminderDelivery Delivery,
    bool DeliveryRequired,
    string? DeliveryInstructions,
    ReminderSchedule Schedule,
    DateTimeOffset? NextFire,
    bool Enabled,
    string? AgentDefinitionId,
    TrustAudience? Audience,
    DateTimeOffset? ExpiresAt = null);

// ── Internal messages ──

/// <summary>
/// Sent by <see cref="ReminderExecutionActor"/> to parent when execution completes.
/// </summary>
internal sealed record ReminderExecutionCompleted(
    Guid ExecutionId,
    ReminderId Id,
    bool Success,
    string? ErrorMessage = null);

/// <summary>
/// Signal emitted by the outbound channel pipeline when a reminder-sourced
/// turn's assistant reply actually flows out through the channel's subscriber
/// sink (e.g., Slack post API returns 200). Used by
/// <see cref="ReminderExecutionActor"/> for <see cref="DeliveryKind.CurrentSession"/>
/// with <see cref="ReminderDefinition.DeliveryRequired"/> = true to gate
/// envelope ack on actual delivery observation.
/// </summary>
/// <param name="ReminderDeliveryKey">
/// Composite key in format "{reminderId}:{fireTimestampMs}".
/// </param>
/// <param name="ChannelType">
/// The channel through which the delivery was observed.
/// </param>
/// <param name="ObservedAtMs">
/// Optional timestamp when the outbound delivery was observed.
/// </param>
public sealed record ReminderDeliveryObserved(
    string ReminderDeliveryKey,
    Channels.ChannelType ChannelType,
    long? ObservedAtMs = null);

// ── Health query ──

/// <summary>
/// Query sent to <see cref="ReminderManagerActor"/> to obtain current health counters.
/// </summary>
public sealed record GetReminderHealthQuery
{
    public static readonly GetReminderHealthQuery Instance = new();
}

/// <summary>
/// Response from <see cref="GetReminderHealthQuery"/> with current runtime counters.
/// </summary>
public sealed record ReminderHealthResponse(
    int ScheduledCount,
    int ActiveExecutions,
    int FailedCount);

// ── Execution history ──

/// <summary>
/// A single execution history entry for a reminder, appended to
/// <c>~/.netclaw/reminders/{id}.history.jsonl</c> after each run.
/// </summary>
public sealed record HistoryRecord(
    DateTimeOffset FiredAt,
    bool Success,
    long DurationMs,
    string SessionId,
    string? ErrorMessage);
