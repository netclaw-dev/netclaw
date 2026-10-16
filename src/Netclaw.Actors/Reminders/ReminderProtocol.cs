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
    [JsonConverter(typeof(JsonStringEnumConverter))]
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
    [JsonConverter(typeof(JsonStringEnumConverter))]
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
    [JsonConverter(typeof(JsonStringEnumConverter))]
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
    /// <see cref="DeliveryKind.Channel"/>, this gates success on the
    /// notification tool being called. It has no effect for
    /// <see cref="DeliveryKind.CurrentSession"/>, where the run ends when the
    /// session admits the fire, and none for <see cref="DeliveryKind.None"/>.
    /// </summary>
    public bool DeliveryRequired { get; init; } = true;

    /// <summary>
    /// Optional guidance for what to include in the delivery to the user.
    /// Content guidance only — never affects routing.
    /// </summary>
    public string? DeliveryInstructions { get; init; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Number of consecutive failed attempts for this reminder, counting both
    /// execution failures and scheduling failures (a failure to compute or install
    /// the next occurrence). A successful execution resets this value.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Terminal result of a reminder that stopped. A recurring reminder keeps it (disabled, <c>Failed</c>)
    /// until an explicit delete. A one-shot holds it only until the next sweep ends it into a tombstone.
    /// Null means that the reminder can still run.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
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
    [JsonConverter(typeof(JsonStringEnumConverter))]
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
    /// Optional expiration for reminders. When set, the reminder
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
/// How a reminder or a one-shot ended. A definition holds <see cref="Completed"/> or <see cref="Failed"/>.
/// <see cref="Cancelled"/> and <see cref="Expired"/> are the ends without a run, and only a
/// <see cref="ReminderTombstone"/> holds them.
/// </summary>
public enum ReminderTerminalOutcome
{
    Completed,
    Failed,
    Cancelled,
    Expired
}

/// <summary>
/// The durable record of an ended one-shot reminder, kept in <c>{RemindersDirectory}/ended/{id}.json</c>
/// for <see cref="ReminderManagerActor.TerminalRetention"/>. See the engineering glossary, "reminder tombstone".
/// The tombstone never holds the delivery target. It holds the prompt only for <see cref="ReminderTerminalOutcome.Failed"/>.
/// </summary>
public sealed record ReminderTombstone
{
    [JsonConverter(typeof(ReminderIdJsonConverter))]
    public required ReminderId Id { get; init; }

    public required string Title { get; init; }

    /// <summary>The audience gate for history, status, cancel, and re-arm of the ended id.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required TrustAudience Audience { get; init; }

    /// <summary>The due time of the ended occurrence. It matches the tombstone to a definition.</summary>
    public required long FireAtMs { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ReminderTerminalOutcome Outcome { get; init; }

    /// <summary>The end time. Retention counts from this time.</summary>
    public required long EndedAtMs { get; init; }

    public string? LastError { get; init; }

    /// <summary>The reminder prompt. Set only when <see cref="Outcome"/> is <see cref="ReminderTerminalOutcome.Failed"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; init; }

    [JsonIgnore]
    public DateTimeOffset EndedAt => DateTimeOffset.FromUnixTimeMilliseconds(EndedAtMs);
}

/// <summary>What a reply says about an ended one-shot (design D7). <see cref="Instructions"/> is set only in a status reply.</summary>
public sealed record ReminderEndedInfo(
    ReminderTerminalOutcome Outcome,
    DateTimeOffset EndedAt,
    DateTimeOffset PurgeAt,
    string? LastError,
    string? Instructions) : INoSerializationVerificationNeeded
{
    /// <summary>
    /// The text of the "not found" reply for an ended one-shot. The REST route, the CLI, and the
    /// enable and disable replies use it, so the words are the same on every surface.
    /// </summary>
    public string DescribeEnd(ReminderId id) =>
        $"Reminder '{id.Value}' ended ({Outcome.ToString().ToLowerInvariant()}) at {EndedAt:u} and its definition was deleted. "
        + $"Its history stays readable until {PurgeAt:u}: netclaw reminder history {id.Value}";

    /// <summary>The reply text of a cancel of an ended one-shot.</summary>
    public string DescribeAlreadyEnded(ReminderId id) =>
        $"Reminder '{id.Value}' already ended ({Outcome.ToString().ToLowerInvariant()} at {EndedAt:u}). Nothing changed.";
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

    /// <summary>
    /// Caller identity for an audience-gated reminder command. The manager compares
    /// <see cref="SourceAudience"/> against a reminder's stored audience before the
    /// command may read or act on it. See
    /// <see cref="ReminderManagerActor.CanAccessAudience"/> for the one place that
    /// comparison happens.
    /// <para>
    /// <see cref="ListRemindersCommand"/>, <see cref="GetReminderCommand"/>,
    /// <see cref="CancelReminderCommand"/>, <see cref="GetReminderStatusQuery"/>,
    /// and <see cref="GetReminderHistoryQuery"/> all require this context. Every
    /// tool (<c>list_reminders</c>, <c>cancel_reminder</c>, <c>get_reminder_history</c>,
    /// <c>set_reminder</c>) and every daemon HTTP endpoint under
    /// <c>/api/reminders</c> builds and passes a real one.
    /// </para>
    /// <para>
    /// A context with a null <see cref="SourceAudience"/> still denies all access
    /// (see <see cref="ReminderManagerActor.CanAccessAudience"/>). Only a real
    /// audience can see or act on a reminder.
    /// </para>
    /// </summary>
    public sealed record ReminderAudienceAuthorizationContext(
        TrustAudience? SourceAudience,
        string? SourceDescription = null) : INoSerializationVerificationNeeded;

    /// <summary>
    /// Cancels a reminder. A one-shot ends with the outcome <see cref="ReminderTerminalOutcome.Cancelled"/>:
    /// the manager writes a tombstone and deletes the schedule and the definition. A recurring reminder is
    /// disabled, and its definition and history stay.
    /// The caller may act on the reminder only when its audience is at or below
    /// <see cref="Authorization"/>'s source audience; otherwise the manager replies
    /// as if the id does not exist — see <see cref="ReminderManagerActor.CanAccessAudience"/>.
    /// </summary>
    public sealed record CancelReminderCommand(
        ReminderId Id,
        ReminderAudienceAuthorizationContext Authorization) : IReminderCommand, INoSerializationVerificationNeeded;

    /// <summary>
    /// Permanently deletes a reminder definition, its schedule, its history, and its tombstone from disk.
    /// Not exposed as an LLM tool — use via CLI (<c>netclaw reminder delete</c>) or HTTP API.
    /// Operator-only surface; no audience gate.
    /// </summary>
    public sealed record DeleteReminderCommand(ReminderId Id) : IReminderCommand, INoSerializationVerificationNeeded;

    /// <summary>Operator-only surface; no audience gate — see <see cref="CancelReminderCommand"/> for the tool-reachable equivalent.</summary>
    public sealed record DisableReminderCommand(ReminderId Id) : IReminderCommand, INoSerializationVerificationNeeded;

    /// <summary>Operator-only surface; no audience gate.</summary>
    public sealed record EnableReminderCommand(ReminderId Id) : IReminderCommand, INoSerializationVerificationNeeded;

    /// <summary>
    /// Lists reminder definitions. Only reminders whose audience is at or below
    /// <see cref="Authorization"/>'s source audience are returned — an out-of-scope
    /// reminder is omitted, not flagged, so its existence is not disclosed.
    /// </summary>
    public sealed record ListRemindersCommand(
        ReminderAudienceAuthorizationContext Authorization,
        bool IncludeDisabled = true) : IReminderQuery, INoSerializationVerificationNeeded;

    // ===== Queries =====

    /// <summary>
    /// Fetches one reminder's full definition. The caller may read it only when its
    /// audience is at or below <see cref="Authorization"/>'s source audience;
    /// otherwise the manager replies as if the id does not exist.
    /// </summary>
    public sealed record GetReminderCommand(
        ReminderId Id,
        ReminderAudienceAuthorizationContext Authorization) : IReminderQuery, INoSerializationVerificationNeeded;

    /// <summary>
    /// Fetches recent execution history for one reminder. The caller may read it
    /// only when its audience is at or below <see cref="Authorization"/>'s source
    /// audience; otherwise the manager replies as if the id does not exist.
    /// </summary>
    public sealed record GetReminderHistoryQuery(
        ReminderId Id,
        int MaxRecords,
        ReminderAudienceAuthorizationContext Authorization) : IReminderQuery, INoSerializationVerificationNeeded;

    // ===== Responses =====

    public sealed record ReminderSavedResponse(
        ReminderId Id,
        string Title,
        bool Success,
        DateTimeOffset? NextFire,
        ReminderSaveError Error = ReminderSaveError.None,
        string? ErrorMessage = null) : IReminderResponse, INoSerializationVerificationNeeded;

    /// <param name="Id">The reminder id.</param>
    /// <param name="Found">False for an unknown id and for an id outside the caller's audience.</param>
    /// <param name="AlreadyEnded">Set when the id is an ended one-shot in the caller's audience. Nothing changed.</param>
    /// <param name="ErrorMessage">Set when the cancel could not finish. The reminder is unchanged.</param>
    /// <param name="Deleted">True when this cancel ended a one-shot. False when it disabled a recurring reminder.</param>
    public sealed record ReminderCancelledResponse(
        ReminderId Id,
        bool Found,
        ReminderEndedInfo? AlreadyEnded = null,
        string? ErrorMessage = null,
        bool Deleted = false) : IReminderResponse, INoSerializationVerificationNeeded;
    public sealed record ReminderDeletedResponse(ReminderId Id, bool Found) : IReminderResponse, INoSerializationVerificationNeeded;

    public sealed record ReminderStateResponse(
        ReminderId Id,
        bool Found,
        bool Enabled,
        DateTimeOffset? NextFire = null,
        string? ErrorMessage = null) : IReminderResponse, INoSerializationVerificationNeeded;

    public sealed record ReminderListResponse(IReadOnlyList<ReminderInfo> Reminders) : IReminderResponse, INoSerializationVerificationNeeded;
    public sealed record GetReminderResponse(ReminderInfo? Reminder, ReminderEndedInfo? Ended = null) : IReminderResponse, INoSerializationVerificationNeeded;

    // ===== Delivery / Health =====

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
    /// The caller may read it only when its audience is at or below
    /// <see cref="Authorization"/>'s source audience; otherwise the manager replies
    /// as if the id does not exist.
    /// </summary>
    public sealed record GetReminderStatusQuery(
        ReminderId Id,
        ReminderAudienceAuthorizationContext Authorization) : IReminderQuery, INoSerializationVerificationNeeded;

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
        IReadOnlyList<HistoryRecord> RecentHistory,
        ReminderEndedInfo? Ended = null) : IReminderResponse, INoSerializationVerificationNeeded;

    /// <summary>
    /// Response to <see cref="GetReminderHistoryQuery"/>. <see cref="Found"/> is
    /// false both when the id does not exist and when it exists but is out of the
    /// caller's audience scope — the two cases are indistinguishable by design.
    /// </summary>
    public sealed record ReminderHistoryResponse(
        ReminderId Id,
        bool Found,
        IReadOnlyList<HistoryRecord> Records) : IReminderResponse, INoSerializationVerificationNeeded;

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
    string? ErrorMessage,
    bool ToolDenied = false)
{
    /// <summary>
    /// The run's status as shown to operators. A run with a denied tool call is never
    /// <c>ok</c>: <see cref="Success"/> is false for it, so a daemon that predates
    /// <see cref="ToolDenied"/> reads the same record as <c>failed</c>.
    /// </summary>
    [JsonIgnore]
    public string Status => ToolDenied ? "denied" : Success ? "ok" : "failed";
}
