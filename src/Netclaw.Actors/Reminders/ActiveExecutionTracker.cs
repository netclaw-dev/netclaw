// -----------------------------------------------------------------------
// <copyright file="ActiveExecutionTracker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Tracks in-flight reminder executions.
/// Enforces the invariant that only one execution of a given reminder runs at a time.
/// </summary>
internal sealed class ActiveExecutionTracker
{
    private readonly HashSet<ReminderId> _executing = [];

    public int Count => _executing.Count;

    public bool IsExecuting(ReminderId reminderId) => _executing.Contains(reminderId);

    public void Add(ReminderId reminderId) => _executing.Add(reminderId);

    /// <returns><c>true</c> if the reminder was tracked and has been removed.</returns>
    public bool Remove(ReminderId reminderId) => _executing.Remove(reminderId);
}
