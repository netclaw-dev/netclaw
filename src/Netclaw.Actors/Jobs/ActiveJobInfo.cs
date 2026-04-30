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
    public required string JobId { get; init; }

    public required string Command { get; init; }

    public required string Rationale { get; init; }

    public required long StartedAtMs { get; init; }

    public TrustAudience Audience { get; init; } = TrustAudience.Personal;

    public string Boundary { get; init; } = SecurityPolicyDefaults.PersonalBoundary;
}
