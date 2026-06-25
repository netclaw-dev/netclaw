// -----------------------------------------------------------------------
// <copyright file="InFlightTurnDedup.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using Netclaw.Actors.Jobs;
using Netclaw.Actors.Reminders;

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Tracks reminder- and background-job-originated turns that are accepted but
/// not yet recorded ("in flight"), so an Akka.Reminders redelivery (or a
/// duplicate job result) is deduplicated before the persisted ledgers
/// (<c>SessionState.ProcessedReminderIds</c> / <c>ProcessedBackgroundJobIds</c>)
/// catch it. Transient and actor-owned: never persisted, rebuilt from journal
/// replay on recovery. Mirrors <see cref="Netclaw.Actors.Reminders.ActiveExecutionTracker"/>.
/// </summary>
internal sealed class InFlightTurnDedup
{
    private readonly HashSet<ReminderId> _reminders = [];
    private readonly HashSet<BackgroundJobId> _backgroundJobs = [];

    public bool IsReminderInFlight(ReminderId? reminderId) =>
        reminderId is { } id && _reminders.Contains(id);

    public void ReserveReminder(ReminderId? reminderId)
    {
        if (reminderId is { } id && !string.IsNullOrEmpty(id.Value))
            _reminders.Add(id);
    }

    public void CompleteReminder(ReminderId? reminderId)
    {
        if (reminderId is { } id)
            _reminders.Remove(id);
    }

    public bool IsBackgroundJobInFlight(BackgroundJobId? backgroundJobId) =>
        backgroundJobId is { } id && _backgroundJobs.Contains(id);

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
