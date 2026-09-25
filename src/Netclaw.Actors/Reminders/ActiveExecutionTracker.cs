// -----------------------------------------------------------------------
// <copyright file="ActiveExecutionTracker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Actors.Reminders;

using Akka.Reminders;

/// <summary>
/// Tracks in-flight reminder executions.
/// Enforces the invariant that only one execution of a given reminder runs at a time.
/// </summary>
internal sealed class ActiveExecutionTracker
{
    private readonly Dictionary<ReminderId, ActiveReminderExecution> _executing = [];

    public int Count => _executing.Count;

    public bool IsExecuting(ReminderId reminderId) => _executing.ContainsKey(reminderId);

    public void Add(
        ReminderId reminderId,
        Guid executionId,
        ReminderEnvelope<ReminderPayload> envelope,
        DateTimeOffset startedAt) =>
        _executing.Add(
            reminderId,
            new ActiveReminderExecution(executionId, envelope, startedAt, ReminderExecutionSource.Scheduled));

    /// <summary>
    /// Records an in-flight <c>netclaw reminder run</c> execution. A manual run
    /// has no Akka.Reminders occurrence behind it, so there is no envelope to
    /// track — carrying a fake one would let settlement Ack/Nack a durable
    /// occurrence that does not exist for this run.
    /// </summary>
    public void AddManual(
        ReminderId reminderId,
        Guid executionId,
        DateTimeOffset startedAt) =>
        _executing.Add(
            reminderId,
            new ActiveReminderExecution(executionId, Envelope: null, startedAt, ReminderExecutionSource.Manual));

    public bool TryGet(ReminderId reminderId, out ActiveReminderExecution execution) =>
        _executing.TryGetValue(reminderId, out execution!);

    public bool TryRemove(
        ReminderId reminderId,
        Guid executionId,
        out ActiveReminderExecution execution)
    {
        if (_executing.TryGetValue(reminderId, out execution!)
            && execution.ExecutionId == executionId)
        {
            _executing.Remove(reminderId);
            return true;
        }

        execution = null!;
        return false;
    }
}

internal sealed record ActiveReminderExecution(
    Guid ExecutionId,
    ReminderEnvelope<ReminderPayload>? Envelope,
    DateTimeOffset StartedAt,
    ReminderExecutionSource Source);
