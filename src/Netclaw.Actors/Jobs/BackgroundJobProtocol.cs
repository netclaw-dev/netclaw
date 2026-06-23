// -----------------------------------------------------------------------
// <copyright file="BackgroundJobProtocol.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Actors.Jobs;

/// <summary>
/// Strongly-typed background job identity.
/// </summary>
public readonly record struct BackgroundJobId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Serializes <see cref="BackgroundJobId"/> as its bare primitive string so the
/// on-disk JSON form is byte-identical to the pre-value-object representation
/// (a raw <c>"id"</c> string, never a nested <c>{ "Value": ... }</c> object).
/// </summary>
public sealed class BackgroundJobIdJsonConverter : JsonConverter<BackgroundJobId>
{
    public override BackgroundJobId Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(
        Utf8JsonWriter writer, BackgroundJobId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public enum BackgroundJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
    Lost,

    /// <summary>
    /// Killed by the system because the owning session passivated — distinct
    /// from <see cref="Cancelled"/> (agent/user-initiated) so the agent can
    /// tell "I stopped this" apart from "the conversation went idle".
    /// </summary>
    Reaped
}

// ── Commands ──

/// <summary>
/// Request to start a background shell command. Sent from the pipeline to
/// <see cref="BackgroundJobManagerActor"/> after approval has been granted.
/// </summary>
public sealed record StartBackgroundJob
{
    public required string Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public required Protocol.SessionId SessionId { get; init; }
    public required string Rationale { get; init; }
    public required TrustAudience Audience { get; init; }
    public required TrustBoundary Boundary { get; init; }
    public required Channels.ChannelType OriginChannelType { get; init; }

    /// <summary>
    /// Optional kill timer. 0 (the default) means no timer: a background job is
    /// a detached process with no completion expectation — it runs until it
    /// exits, is cancelled, or its owning session passivates. Only an explicit
    /// positive <c>_timeout_seconds</c> hint arms a timer.
    /// </summary>
    public int TimeoutSeconds { get; init; }

    public Protocol.SenderId? SenderId { get; init; }
}

/// <summary>
/// Request to cancel a running background job.
/// </summary>
public sealed record CancelBackgroundJob(
    BackgroundJobId JobId,
    Protocol.SessionId SessionId,
    TrustAudience Audience,
    TrustBoundary Boundary);

/// <summary>
/// Query for current status of a background job.
/// </summary>
public sealed record QueryBackgroundJob(BackgroundJobId JobId, Protocol.SessionId SessionId, TrustAudience Audience, TrustBoundary Boundary);

/// <summary>
/// Sent by a passivating session: kill every running or pending job this
/// session owns and mark them <see cref="BackgroundJobStatus.Reaped"/>.
/// Reaped jobs produce NO completion delivery — delivering a turn would
/// rehydrate the session that is tearing itself down.
/// </summary>
public sealed record KillJobsForSession(Protocol.SessionId SessionId);

/// <summary>
/// Acknowledgement for <see cref="KillJobsForSession"/>: kills have been
/// initiated and definitions marked. The passivating session waits for this
/// before taking its final snapshot.
/// </summary>
public sealed record SessionJobsReaped(Protocol.SessionId SessionId, int ReapedCount);

// ── Responses ──

/// <summary>
/// Confirmation that a background job was accepted for execution.
/// <see cref="OutputLogPath"/> is handed back to the agent in the submit ACK so
/// it can monitor the streaming log without an extra status query.
/// </summary>
public sealed record BackgroundJobStarted(BackgroundJobId JobId, string? OutputLogPath = null);

/// <summary>
/// Status response for a background job query.
/// </summary>
public sealed record BackgroundJobStatusResponse
{
    public required BackgroundJobId JobId { get; init; }
    public required BackgroundJobStatus Status { get; init; }
    public bool Found { get; init; } = true;
    public int? ExitCode { get; init; }
    public string? OutputTail { get; init; }
    public string? OutputFilePath { get; init; }
    public TimeSpan? Elapsed { get; init; }
    public string? Rationale { get; init; }
}

public sealed record BackgroundJobCancelResponse(BackgroundJobId JobId, bool Found);

// ── Internal messages ──

/// <summary>
/// Sent by <see cref="BackgroundJobExecutionActor"/> to parent when execution completes.
/// </summary>
internal sealed record BackgroundJobCompleted
{
    public required BackgroundJobId JobId { get; init; }
    public required BackgroundJobStatus Status { get; init; }
    public int ExitCode { get; init; }
    public string? OutputTail { get; init; }
    public string? OutputFilePath { get; init; }
    public TimeSpan Duration { get; init; }
}

// ── Persistence ──

/// <summary>
/// Job definition persisted to <c>~/.netclaw/jobs/{id}.json</c>.
/// </summary>
public sealed record BackgroundJobDefinition
{
    [JsonConverter(typeof(BackgroundJobIdJsonConverter))]
    public required BackgroundJobId Id { get; init; }
    public required string Command { get; init; }
    public string? WorkingDirectory { get; init; }
    [JsonConverter(typeof(Protocol.SessionIdJsonConverter))]
    public required Protocol.SessionId SessionId { get; init; }
    public required string Rationale { get; init; }
    public BackgroundJobStatus Status { get; init; } = BackgroundJobStatus.Pending;
    public int? ExitCode { get; init; }

    /// <summary>0 = no kill timer (detached process; see <see cref="StartBackgroundJob.TimeoutSeconds"/>).</summary>
    public int TimeoutSeconds { get; init; }
    public long StartedAtMs { get; init; }
    public long? CompletedAtMs { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required TrustAudience Audience { get; init; }

    [JsonConverter(typeof(TrustBoundaryJsonConverter))]
    public required TrustBoundary Boundary { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Channels.ChannelType OriginChannelType { get; init; }

    [JsonConverter(typeof(Protocol.SenderIdJsonConverter))]
    public Protocol.SenderId? SenderId { get; init; }

    [JsonIgnore]
    public DateTimeOffset StartedAt => DateTimeOffset.FromUnixTimeMilliseconds(StartedAtMs);

    [JsonIgnore]
    public DateTimeOffset? CompletedAt =>
        CompletedAtMs is not null ? DateTimeOffset.FromUnixTimeMilliseconds(CompletedAtMs.Value) : null;
}

/// <summary>Ping sent to <see cref="BackgroundJobManagerActor"/> to confirm it is ready (PreStart + startup reconciliation complete).</summary>
public sealed record GetBackgroundJobManagerHealth : INoSerializationVerificationNeeded
{
    public static readonly GetBackgroundJobManagerHealth Instance = new();
    private GetBackgroundJobManagerHealth() { }
}

/// <summary>Response from <see cref="GetBackgroundJobManagerHealth"/> with current runtime counters.</summary>
public sealed record BackgroundJobManagerHealthResponse(
    int ActiveJobCount,
    int QueuedJobCount) : INoSerializationVerificationNeeded;
