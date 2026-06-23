// -----------------------------------------------------------------------
// <copyright file="ActiveJobInfo.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Jobs;

/// <summary>
/// Lightweight record persisted in <c>SessionState.ActiveBackgroundJobs</c>
/// so the LLM knows what it's waiting for after compaction or session resumption.
/// </summary>
public sealed record ActiveJobInfo
{
    public required BackgroundJobId JobId { get; init; }

    public required string Command { get; init; }

    public required string Rationale { get; init; }

    public required long StartedAtMs { get; init; }

    public required TrustAudience Audience { get; init; }

    public required TrustBoundary Boundary { get; init; }

    /// <summary>
    /// Path to the job's streaming output log, surfaced in the active-jobs
    /// context block so the agent can monitor without a status query.
    /// </summary>
    public string? OutputLogPath { get; init; }

    /// <summary>
    /// Set when the job was killed at session passivation. A reaped entry is
    /// surfaced once in the context block on the next rehydration so the agent
    /// learns its process is gone, then pruned after the next completed turn.
    /// </summary>
    public long? ReapedAtMs { get; init; }
}
