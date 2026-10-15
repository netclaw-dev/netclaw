// -----------------------------------------------------------------------
// <copyright file="InFlightTurnDedup.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using Netclaw.Actors.Jobs;

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Tracks background-job-originated turns that are accepted but not yet recorded ("in flight"), so a duplicate job
/// result is deduplicated before the persisted ledger (<c>SessionState.ProcessedBackgroundJobIds</c>) catches it.
/// The input owner keeps this transient set. Journal replay rebuilds the ledger after recovery.
/// Reserve ignores an empty id. Complete and lookup do not.
/// </summary>
internal sealed class InFlightTurnDedup
{
    private readonly HashSet<BackgroundJobId> _backgroundJobs = [];

    public bool IsBackgroundJobInFlight(BackgroundJobId? backgroundJobId)
        => backgroundJobId is { } id && _backgroundJobs.Contains(id);

    public void ReserveBackgroundJob(BackgroundJobId? backgroundJobId)
    {
        if (backgroundJobId is { } id && !string.IsNullOrEmpty(id.Value))
            _backgroundJobs.Add(id);
    }

    public void CompleteBackgroundJob(BackgroundJobId? backgroundJobId)
    {
        if (backgroundJobId is { } id)
            _backgroundJobs.Remove(id);
    }
}
