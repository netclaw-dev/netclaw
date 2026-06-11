// -----------------------------------------------------------------------
// <copyright file="StreamingToolWatchdog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Inactivity budget for one tool call: <see cref="FirstItem"/> bounds the wait
/// for the first <see cref="ToolCallUpdate"/>, <see cref="InterItem"/> the gap
/// between later items. <see cref="Flat"/> uses one value for both. A
/// non-positive budget disables the watchdog.
/// </summary>
internal readonly record struct ToolWatchdogBudget(TimeSpan FirstItem, TimeSpan InterItem)
{
    public static ToolWatchdogBudget Flat(TimeSpan budget) => new(budget, budget);
}

/// <summary>
/// Consumes one tool call's <see cref="ToolCallUpdate"/> stream under a per-call
/// inactivity watchdog and returns the terminal completion result.
///
/// This is the only liveness control for a tool call — there is no batch-level
/// watchdog, so a healthy parallel call cannot extend (or mask) a stalled
/// sibling. A periodic timer polls a last-activity timestamp rather than being
/// reset on each item: resetting a timer cannot recall an already-elapsed
/// callback, so a reset racing the callback could cancel a still-live call —
/// polling has no such race. The timer comes from the supplied
/// <see cref="TimeProvider"/> so it can be virtualized in tests. A tool whose
/// iterator ignores the enumerator's cancellation token cannot be force-stopped.
/// </summary>
internal static class StreamingToolWatchdog
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public static async Task<string> ConsumeAsync(
        IAsyncEnumerable<ToolCallUpdate> stream,
        string toolName,
        ToolWatchdogBudget budget,
        TimeProvider timeProvider,
        Action<ToolActivityUpdate>? onActivity,
        CancellationToken ct)
    {
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Shared with the timer callback. Both are long fields (atomic reads/
        // writes); the consumer writes them on each item, the callback reads them.
        var lastActivity = timeProvider.GetTimestamp();
        var budgetTicks = budget.FirstItem.Ticks;

        // `await using`, declared after watchdogCts so it disposes first:
        // ITimer.DisposeAsync waits for any in-flight callback to finish, so the
        // callback's Cancel() can never race a disposed token source. The
        // callback never touches the timer, so there is no reset/disposal race.
        await using var timer = timeProvider.CreateTimer(
            _ =>
            {
                var allowed = TimeSpan.FromTicks(Volatile.Read(ref budgetTicks));
                if (allowed > TimeSpan.Zero
                    && timeProvider.GetElapsedTime(Volatile.Read(ref lastActivity)) >= allowed)
                {
                    watchdogCts.Cancel();
                }
            },
            state: null,
            PollInterval,
            PollInterval);

        string? result = null;
        var enumerator = stream.GetAsyncEnumerator(watchdogCts.Token);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                    when (watchdogCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Watchdog fired (not caller cancellation): report a timeout.
                    var stalled = TimeSpan.FromTicks(Volatile.Read(ref budgetTicks));
                    throw new TimeoutException(
                        $"Tool '{toolName}' produced no activity for {stalled.TotalSeconds:F0}s "
                        + "and was stopped. It may be stuck — please try again, or simplify the request.");
                }

                if (!hasNext)
                    break;

                // Any item is liveness; later items are held to the tighter budget.
                Volatile.Write(ref budgetTicks, budget.InterItem.Ticks);
                Volatile.Write(ref lastActivity, timeProvider.GetTimestamp());

                switch (enumerator.Current)
                {
                    case ToolCompletedUpdate completed:
                        return completed.Result;
                    case ToolActivityUpdate activity:
                        if (activity.SuspendsInactivityWatchdog)
                            Volatile.Write(ref budgetTicks, TimeSpan.Zero.Ticks);
                        onActivity?.Invoke(activity);
                        break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        // A stream that ends with no completion item violates the tool-call
        // contract — fail loudly rather than synthesizing a result.
        return result ?? throw new InvalidOperationException(
            $"Tool '{toolName}' stream ended without a completion item.");
    }
}
