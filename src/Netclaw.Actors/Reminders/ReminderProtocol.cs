// -----------------------------------------------------------------------
// <copyright file="ReminderProtocol.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;
using Netclaw.Actors.Serialization;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Strongly-typed reminder identity.
/// </summary>
public readonly record struct ReminderId(string Value) : INetclawSerializableMessage
{
    public override string ToString() => Value;
}

/// <summary>
/// Serializes <see cref="ReminderId"/> as its bare primitive string so the
/// on-disk JSON form is byte-identical to the pre-value-object representation
/// (a raw <c>"id"</c> string, never a nested <c>{ "Value": ... }</c> object).
/// </summary>
public sealed class ReminderIdJsonConverter : JsonConverter<ReminderId>
{
    public override ReminderId Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(
        Utf8JsonWriter writer, ReminderId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// How reminder results are delivered.
/// </summary>
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
public sealed record ReminderDelivery : INetclawSerializableMessage
{
    /// <summary>
    /// How results are delivered.
    /// </summary>
    public DeliveryKind Kind { get; init; }

    /// <summary>
    /// Transport identifier for Channel delivery (e.g., "slack").
    /// Null for CurrentSession and None.
    /// </summary>
    public string? Transport { get; init; }

    /// <summary>
    /// Canonical target address for Channel delivery (e.g., "C0123ABC").
    /// Resolved and validated at set time. Null for CurrentSession and None.
    /// </summary>
    public string? Address { get; init; }

    /// <summary>
    /// Resolved standard channel delivery target for Channel delivery. Older
    /// persisted reminders may only have <see cref="Transport"/> and
    /// <see cref="Address"/>; execution still handles those fields but new
    /// reminders store this explicit target for trigger-source routing checks.
    /// </summary>
    public ChannelDeliveryTargetInfo? Target { get; init; }

    /// <summary>
    /// Session ID for CurrentSession delivery. Null for Channel and None.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Channel type of the originating session for CurrentSession delivery.
    /// Used to route DeliverTrustedSessionTurn to the correct gateway.
    /// Null for Channel and None.
    /// </summary>
    public Channels.ChannelType? OriginChannelType { get; init; }

    /// <summary>
    /// Gets the notification tool name for Channel delivery.
    /// Returns null for non-Channel delivery kinds.
    /// </summary>
    public string? GetNotificationToolName() => Kind == DeliveryKind.Channel
        ? "send_channel_message"
        : null;
}

/// <summary>
/// The type of schedule for a reminder.
/// </summary>
public enum ReminderScheduleType
{
    OneShot = 0,
    Interval = 1,
    Cron = 2
}

/// <summary>
/// Describes when and how a reminder fires.
/// </summary>
public sealed record ReminderSchedule : INetclawSerializableMessage
{
    public ReminderScheduleType Type { get; init; }

    /// <summary>
    /// For OneShot: the absolute fire time.
    /// For Interval: optional explicit first fire.
    /// For Cron: not used.
    /// </summary>
    public long? FireAtMs { get; init; }

    public DateTimeOffset? FireAt
    {
        get => FireAtMs is not null ? DateTimeOffset.FromUnixTimeMilliseconds(FireAtMs.Value) : null;
        init => FireAtMs = value?.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// For Interval schedules: repeat interval.
    /// </summary>
    public long? IntervalTicks { get; init; }

    public TimeSpan? Interval
    {
        get => IntervalTicks is not null ? TimeSpan.FromTicks(IntervalTicks.Value) : null;
        init => IntervalTicks = value?.Ticks;
    }

    /// <summary>
    /// For Cron schedules: cron expression.
    /// </summary>
    public string? CronExpression { get; init; }

    /// <summary>
    /// Original expression as entered by user (e.g. "6h", "0 */6 * * *").
    /// </summary>
    public string? OriginalExpression { get; init; }
}

/// <summary>
/// Canonical reminder definition persisted to disk.
/// </summary>
public sealed record ReminderDefinition
{
    [JsonConverter(typeof(ReminderIdJsonConverter))]
    public required ReminderId Id { get; init; }
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
    /// on the <see cref="ReminderDeliveryResult"/> signal. For
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
    /// Number of consecutive failed execution attempts for this reminder.
    /// A successful attempt resets this value.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Terminal result for a retained one-shot reminder.
    /// Null means that the reminder can still run.
    /// </summary>
    public ReminderTerminalOutcome? TerminalOutcome { get; set; }

    /// <summary>
    /// Deferred shadow field for selecting specialized agent behavior.
    /// Tracked by issue #147.
    /// </summary>
    public string? AgentDefinitionId { get; init; }

    /// <summary>
    /// Persisted execution audience for this reminder.
    /// Conversational and tool-created reminders inherit the creating
    /// session/channel audience at mint time. Legacy documents missing this
    /// field are rejected at load and are never scheduled.
    /// </summary>
    public required TrustAudience Audience { get; init; }

    /// <summary>
    /// Persisted execution boundary for this reminder.
    /// For Mode B reminders this mirrors the creating session's effective trust
    /// boundary so reminder re-entry does not widen scope. Legacy documents
    /// missing this field are rejected at load and are never scheduled.
    /// </summary>
    [JsonConverter(typeof(TrustBoundaryJsonConverter))]
    public required TrustBoundary Boundary { get; init; }

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

public enum ReminderTerminalOutcome
{
    Completed,
    Failed
}

/// <summary>
/// Message persisted inside Akka.Reminders. Intentionally lightweight: pointer to disk definition.
/// </summary>
public sealed record ReminderPayload : INetclawSerializableMessage
{
    public ReminderId Id { get; init; }
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

/// <summary>
/// External message contract for <see cref="ReminderManagerActor"/>.
/// </summary>
public static partial class ReminderProtocol
{

    /// <summary>Marker for reminder commands.</summary>
    public interface IReminderCommand;

    /// <summary>Marker for reminder queries.</summary>
    public interface IReminderQuery;

    /// <summary>Marker for reminder responses.</summary>
    public interface IReminderResponse;

    // ===== Commands =====

    public sealed record SaveReminderCommand(
        ReminderDefinition Definition,
        ReminderWriteMode WriteMode = ReminderWriteMode.CreateOnly,
        ReminderAudienceAuthorizationContext? Authorization = null) : IReminderCommand, INoSerializationVerificationNeeded;

    public sealed record ReminderAudienceAuthorizationContext(
        TrustAudience? SourceAudience,
        string? SourceDescription = null) : INoSerializationVerificationNeeded;

    /// <summary>
    /// Disables a reminder and cancels any active schedule. The definition file
    /// is preserved on disk so history and configuration remain available for diagnosis.
    /// </summary>
    public sealed record CancelReminderCommand(ReminderId Id) : IReminderCommand, INoSerializationVerificationNeeded;

    /// <summary>
    /// Permanently deletes a reminder definition, its schedule, and history from disk.
    /// Not exposed as an LLM tool — use via CLI (<c>netclaw reminder delete</c>) or HTTP API.
    /// </summary>
    public sealed record DeleteReminderCommand(ReminderId Id) : IReminderCommand, INoSerializationVerificationNeeded;
    public sealed record DisableReminderCommand(ReminderId Id) : IReminderCommand, INoSerializationVerificationNeeded;
    public sealed record EnableReminderCommand(ReminderId Id) : IReminderCommand, INoSerializationVerificationNeeded;
    public sealed record ListRemindersCommand(bool IncludeDisabled = true) : IReminderQuery, INoSerializationVerificationNeeded;

    // ===== Queries =====

    public sealed record GetReminderCommand(ReminderId Id) : IReminderQuery, INoSerializationVerificationNeeded;

    // ===== Responses =====

    public sealed record ReminderSavedResponse(
        ReminderId Id,
        string Title,
        bool Success,
        DateTimeOffset? NextFire,
        ReminderSaveError Error = ReminderSaveError.None,
        string? ErrorMessage = null) : IReminderResponse, INoSerializationVerificationNeeded;

    public sealed record ReminderCancelledResponse(ReminderId Id, bool Found) : IReminderResponse, INoSerializationVerificationNeeded;
    public sealed record ReminderDeletedResponse(ReminderId Id, bool Found) : IReminderResponse, INoSerializationVerificationNeeded;

    public sealed record ReminderStateResponse(
        ReminderId Id,
        bool Found,
        bool Enabled,
        DateTimeOffset? NextFire = null,
        string? ErrorMessage = null) : IReminderResponse, INoSerializationVerificationNeeded;

    public sealed record ReminderListResponse(IReadOnlyList<ReminderInfo> Reminders) : IReminderResponse, INoSerializationVerificationNeeded;
    public sealed record GetReminderResponse(ReminderInfo? Reminder) : IReminderResponse, INoSerializationVerificationNeeded;

    // ===== Delivery / Health =====

    /// <summary>
    /// Point-to-point delivery outcome sent by a channel binding actor directly
    /// back to the dispatching <see cref="ReminderExecutionActor"/> (carried as
    /// <see cref="Channels.MessageSource.DeliveryObserver"/> on the originating
    /// <c>DeliverTrustedSessionTurn</c>) when a reminder-sourced turn completes.
    /// Used for <see cref="DeliveryKind.CurrentSession"/> with
    /// <see cref="ReminderDefinition.DeliveryRequired"/> = true to gate envelope
    /// ack on whether the assistant reply actually reached the channel.
    /// <para>
    /// Unlike the prior EventStream-broadcast observation, this signal reports
    /// <see cref="Delivered"/> = false on a failed post, so the execution actor
    /// can report failure immediately (triggering Akka.Reminders redelivery)
    /// instead of waiting out the backstop timeout.
    /// </para>
    /// </summary>
    /// <param name="ReminderDeliveryKey">
    /// Composite key in format "{reminderId}:{fireTimestampMs}".
    /// </param>
    /// <param name="ChannelType">
    /// The channel that attempted the delivery.
    /// </param>
    /// <param name="Delivered">
    /// True when the assistant reply was posted to the channel; false when the
    /// turn completed without a successful post.
    /// </param>
    /// <param name="FailureReason">
    /// Optional human-readable reason when <see cref="Delivered"/> is false.
    /// </param>
    /// <param name="ObservedAtMs">
    /// Optional timestamp when the outbound delivery outcome was observed.
    /// </param>
    public sealed record ReminderDeliveryResult(
        ReminderId ReminderDeliveryKey,
        Channels.ChannelType ChannelType,
        bool Delivered,
        string? FailureReason = null,
        long? ObservedAtMs = null) : IReminderResponse, INoSerializationVerificationNeeded;

    // ===== Health query =====

    /// <summary>
    /// Query sent to <see cref="ReminderManagerActor"/> to obtain current health counters.
    /// </summary>
    public sealed record GetReminderHealthQuery : IReminderQuery, INoSerializationVerificationNeeded
    {
        public static readonly GetReminderHealthQuery Instance = new();
    }

    /// <summary>
    /// Response from <see cref="GetReminderHealthQuery"/> with current runtime counters.
    /// </summary>
    public sealed record ReminderHealthResponse(
        int ScheduledCount,
        int ActiveExecutions,
        int FailedCount) : IReminderResponse, INoSerializationVerificationNeeded;

    /// <summary>
    /// Query sent to <see cref="ReminderManagerActor"/> for the per-reminder
    /// operational status surfaced by <c>netclaw reminder status &lt;id&gt;</c>.
    /// </summary>
    public sealed record GetReminderStatusQuery(ReminderId Id) : IReminderQuery, INoSerializationVerificationNeeded;

    /// <summary>
    /// Response to <see cref="GetReminderStatusQuery"/>: per-reminder health for an
    /// operator — whether the reminder exists/is enabled, whether an execution is in
    /// flight right now, when it next fires, the durable failure count, the
    /// process-local skipped occurrence count, and recent run history.
    /// </summary>
    public sealed record ReminderStatusResponse(
        ReminderId Id,
        bool Found,
        bool Enabled,
        bool Executing,
        DateTimeOffset? NextFire,
        int ConsecutiveFailures,
        int SkippedDuplicates,
        ReminderTerminalOutcome? TerminalOutcome,
        ReminderOccurrenceInfo? Occurrence,
        IReadOnlyList<HistoryRecord> RecentHistory) : IReminderResponse, INoSerializationVerificationNeeded;

}

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
    DateTimeOffset? ExpiresAt = null,
    int ConsecutiveFailures = 0,
    ReminderTerminalOutcome? TerminalOutcome = null) : INoSerializationVerificationNeeded;

// ── Internal messages ──

/// <summary>
/// Sent by <see cref="ReminderExecutionActor"/> to parent when execution completes.
/// </summary>
internal sealed record ReminderExecutionCompleted(
    Guid ExecutionId,
    ReminderId Id,
    bool Success,
    HistoryRecord History,
    string? ErrorMessage = null) : INoSerializationVerificationNeeded;

internal sealed record ReminderExecutionAccepted(Guid ExecutionId) : INoSerializationVerificationNeeded;

internal sealed record ReminderExecutionTerminated(
    Guid ExecutionId,
    ReminderId Id) : INoSerializationVerificationNeeded;

/// <summary>
/// Durable state for the most relevant Akka.Reminders occurrence.
/// </summary>
public sealed record ReminderOccurrenceInfo(
    DateTimeOffset DueTimeUtc,
    DateTimeOffset? NextAttemptAtUtc,
    int AttemptCount,
    string? LastFailureReason,
    string CompletionStatus,
    DateTimeOffset? DeliveryDeadlineUtc,
    DateTimeOffset? AckDeadlineUtc,
    DateTimeOffset? CompletedAtUtc) : INoSerializationVerificationNeeded;

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
