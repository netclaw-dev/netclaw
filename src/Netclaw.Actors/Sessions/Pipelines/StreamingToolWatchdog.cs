// -----------------------------------------------------------------------
// <copyright file="StreamingToolWatchdog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions.Pipelines;

internal enum ToolWatchdogResetMode
{
    ResetOnItem,
    WallClock,
    FirstItemOnly
}

/// <summary>
/// Budget for one tool-call stream. Opaque tools use <see cref="WallClock"/> so
/// output cannot keep them alive forever. Self-monitoring tools use
/// <see cref="FirstItemOnly"/> so the parent only bounds the blind startup
/// window; after that, the tool's own watchdog reports terminal success/failure.
/// </summary>
internal readonly record struct ToolWatchdogBudget(TimeSpan FirstItem, TimeSpan InterItem, ToolWatchdogResetMode ResetMode)
{
    public ToolWatchdogBudget(TimeSpan firstItem, TimeSpan interItem)
        : this(firstItem, interItem, ToolWatchdogResetMode.ResetOnItem)
    {
    }

    public static ToolWatchdogBudget Flat(TimeSpan budget) => new(budget, budget, ToolWatchdogResetMode.ResetOnItem);
    public static ToolWatchdogBudget WallClock(TimeSpan budget) => new(budget, budget, ToolWatchdogResetMode.WallClock);
    public static ToolWatchdogBudget FirstItemOnly(TimeSpan budget) => new(budget, TimeSpan.Zero, ToolWatchdogResetMode.FirstItemOnly);
}

/// <summary>
/// Consumes one tool call's <see cref="ToolCallUpdate"/> stream under a per-call
/// watchdog and returns the terminal completion result.
///
/// There is no batch-level watchdog, so sibling calls are monitored
/// independently. The timer comes from the supplied <see cref="TimeProvider"/>
/// so it can be virtualized in tests. A tool whose iterator ignores the
/// enumerator's cancellation token cannot be force-stopped.
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
        // writes); the consumer updates them according to the selected reset mode,
        // the callback reads them.
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
                    throw BuildTimeoutException(toolName, budget.ResetMode, TimeSpan.FromTicks(Volatile.Read(ref budgetTicks)));
                }

                if (!hasNext)
                    break;

                ApplyItemReset(budget, timeProvider, ref lastActivity, ref budgetTicks);

                switch (enumerator.Current)
                {
                    case ToolCompletedUpdate completed:
                        return completed.Result;
                    case ToolActivityUpdate activity:
                        // An explicit suspend (a tool blocked on human approval) pauses
                        // the watchdog in EVERY mode. A human-in-the-loop wait is an
                        // external block, not the tool running away on its own output, so
                        // it must never burn the budget — including WallClock. This is
                        // deliberately NOT mode-gated: ordinary per-item resets stay
                        // mode-gated in ApplyItemReset (WallClock still ignores those), but
                        // the explicit approval-pause signal is honored everywhere.
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

    private static void ApplyItemReset(
        ToolWatchdogBudget budget,
        TimeProvider timeProvider,
        ref long lastActivity,
        ref long budgetTicks)
    {
        switch (budget.ResetMode)
        {
            case ToolWatchdogResetMode.ResetOnItem:
                Volatile.Write(ref budgetTicks, budget.InterItem.Ticks);
                Volatile.Write(ref lastActivity, timeProvider.GetTimestamp());
                break;
            case ToolWatchdogResetMode.FirstItemOnly:
                Volatile.Write(ref budgetTicks, TimeSpan.Zero.Ticks);
                break;
            case ToolWatchdogResetMode.WallClock:
                break;
        }
    }

    private static TimeoutException BuildTimeoutException(string toolName, ToolWatchdogResetMode mode, TimeSpan timeout)
    {
        var message = mode switch
        {
            ToolWatchdogResetMode.WallClock =>
                $"Tool '{toolName}' exceeded execution budget of {timeout.TotalSeconds:F0}s and was stopped.",
            ToolWatchdogResetMode.FirstItemOnly =>
                $"Tool '{toolName}' produced no startup activity for {timeout.TotalSeconds:F0}s and was stopped.",
            _ =>
                $"Tool '{toolName}' produced no activity for {timeout.TotalSeconds:F0}s and was stopped. It may be stuck — please try again, or simplify the request."
        };

        return new TimeoutException(message);
    }
}
