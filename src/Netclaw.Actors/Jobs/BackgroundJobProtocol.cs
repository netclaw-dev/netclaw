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

public enum BackgroundJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
    Lost
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
    public required string Boundary { get; init; }
    public required Channels.ChannelType OriginChannelType { get; init; }
    public int TimeoutSeconds { get; init; } = 600;
    public string? SenderId { get; init; }
}

/// <summary>
/// Request to cancel a running background job.
/// </summary>
public sealed record CancelBackgroundJob(
    BackgroundJobId JobId,
    Protocol.SessionId SessionId,
    TrustAudience Audience,
    string Boundary);

/// <summary>
/// Query for current status of a background job.
/// </summary>
public sealed record QueryBackgroundJob(BackgroundJobId JobId, Protocol.SessionId SessionId, TrustAudience Audience, string Boundary);

// ── Responses ──

/// <summary>
/// Confirmation that a background job was accepted for execution.
/// </summary>
public sealed record BackgroundJobStarted(BackgroundJobId JobId);

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
    public required string Id { get; init; }
    public required string Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public required string SessionId { get; init; }
    public required string Rationale { get; init; }
    public BackgroundJobStatus Status { get; init; } = BackgroundJobStatus.Pending;
    public int? ExitCode { get; init; }
    public int TimeoutSeconds { get; init; } = 600;
    public long StartedAtMs { get; init; }
    public long? CompletedAtMs { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TrustAudience Audience { get; init; } = TrustAudience.Personal;
    public string Boundary { get; init; } = SecurityPolicyDefaults.PersonalBoundary;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Channels.ChannelType OriginChannelType { get; init; }

    public string? SenderId { get; init; }

    [JsonIgnore]
    public DateTimeOffset StartedAt => DateTimeOffset.FromUnixTimeMilliseconds(StartedAtMs);

    [JsonIgnore]
    public DateTimeOffset? CompletedAt =>
        CompletedAtMs is not null ? DateTimeOffset.FromUnixTimeMilliseconds(CompletedAtMs.Value) : null;
}
