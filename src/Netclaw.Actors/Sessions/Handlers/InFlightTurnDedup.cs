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
    // Both ledgers share one guard implementation so the reserve/complete/lookup
    // semantics (notably: reserve ignores empty ids, complete/lookup don't) can't
    // drift between the reminder and background-job paths.
    private readonly InFlightSet<ReminderId> _reminders = new(id => id.Value);
    private readonly InFlightSet<BackgroundJobId> _backgroundJobs = new(id => id.Value);

    public bool IsReminderInFlight(ReminderId? reminderId) => _reminders.Contains(reminderId);
    public void ReserveReminder(ReminderId? reminderId) => _reminders.Reserve(reminderId);
    public void CompleteReminder(ReminderId? reminderId) => _reminders.Remove(reminderId);

    public bool IsBackgroundJobInFlight(BackgroundJobId? backgroundJobId) => _backgroundJobs.Contains(backgroundJobId);
    public void ReserveBackgroundJob(BackgroundJobId? backgroundJobId) => _backgroundJobs.Reserve(backgroundJobId);
    public void CompleteBackgroundJob(BackgroundJobId? backgroundJobId) => _backgroundJobs.Remove(backgroundJobId);

    private sealed class InFlightSet<T>(Func<T, string> valueOf)
        where T : struct
    {
        private readonly HashSet<T> _ids = [];

        public bool Contains(T? id) => id is { } value && _ids.Contains(value);

        public void Reserve(T? id)
        {
            if (id is { } value && !string.IsNullOrEmpty(valueOf(value)))
                _ids.Add(value);
        }

        public void Remove(T? id)
        {
            if (id is { } value)
                _ids.Remove(value);
        }
    }
}
