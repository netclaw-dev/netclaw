// -----------------------------------------------------------------------
// <copyright file="StreamingToolWatchdog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Consumes a single tool call's <see cref="ToolCallUpdate"/> stream under a
/// per-call, two-phase inactivity watchdog: the call must produce its first
/// item within <c>firstItemBudget</c>, and every subsequent item resets the
/// timer to <c>interItemBudget</c>. Inactivity past the current budget cancels
/// the call and surfaces a <see cref="TimeoutException"/>.
///
/// This is the only liveness control for a tool call — there is no batch-level
/// watchdog. Each call has its own watchdog, so a healthy parallel call cannot
/// extend (or mask) a stalled sibling. The timer is created through the supplied
/// <see cref="TimeProvider"/> so it can be virtualized in tests.
/// </summary>
internal static class StreamingToolWatchdog
{
    /// <summary>
    /// Enumerate <paramref name="stream"/> under the inactivity watchdog and
    /// return the terminal completion result. Activity items reset the watchdog
    /// and are forwarded to <paramref name="onActivity"/>; only the terminal
    /// <see cref="ToolCompletedUpdate"/> contributes the returned result.
    /// </summary>
    public static async Task<string> ConsumeAsync(
        IAsyncEnumerable<ToolCallUpdate> stream,
        string toolName,
        TimeSpan firstItemBudget,
        TimeSpan interItemBudget,
        TimeProvider timeProvider,
        Action<ToolActivityUpdate>? onActivity,
        CancellationToken ct)
    {
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // `await using` the timer: ITimer.DisposeAsync waits for any in-flight
        // callback to finish before `watchdogCts` (declared above it, so disposed
        // after it) is disposed, so the callback's Cancel() can never race a
        // disposed token source.
        await using var timer = timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            watchdogCts,
            firstItemBudget,
            Timeout.InfiniteTimeSpan);

        var currentBudget = firstItemBudget;
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
                    throw new TimeoutException(
                        $"Tool '{toolName}' produced no activity for "
                        + $"{currentBudget.TotalSeconds:F0}s and was stopped. It may be stuck — "
                        + "please try again, or simplify the request.");
                }

                if (!hasNext)
                    break;

                // Any item — activity or completion — is liveness: reset the
                // watchdog to the (tighter) inter-item budget.
                currentBudget = interItemBudget;
                timer.Change(interItemBudget, Timeout.InfiniteTimeSpan);

                switch (enumerator.Current)
                {
                    case ToolCompletedUpdate completed:
                        result = completed.Result;
                        break;
                    case ToolActivityUpdate activity:
                        onActivity?.Invoke(activity);
                        break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        return result ?? $"Tool '{toolName}' completed without producing a result.";
    }
}
