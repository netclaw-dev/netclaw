// -----------------------------------------------------------------------
// <copyright file="StreamTaskObservation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Streams.Dsl;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Observes discarded Akka.Streams sink tasks. Stream teardown can fault these
/// tasks after the owner receives the primary termination signal.
/// </summary>
internal static class StreamTaskObservation
{
    /// <summary>
    /// Attach a fault-only continuation that reads <see cref="Task.Exception"/>.
    /// </summary>
    public static void ObserveSilently(Task task)
    {
        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Replace a sink task with <see cref="NotUsed"/> after the observer attaches.
    /// </summary>
    public static Sink<TIn, NotUsed> ObservingFault<TIn>(this Sink<TIn, Task<Done>> sink) =>
        sink.MapMaterializedValue<NotUsed>(static task =>
        {
            ObserveSilently(task);
            return NotUsed.Instance;
        });
}
