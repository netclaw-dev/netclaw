// -----------------------------------------------------------------------
// <copyright file="FakeClockDriver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;

namespace Netclaw.Tests.Utilities;

internal static class FakeClockDriver
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Calls <paramref name="advanceClock"/> until <paramref name="task"/> finishes. A task that waits on a
    /// fake clock only moves when the test advances that clock. The wait is bounded, so a regression that
    /// waits for ever fails the test with a clear message instead of a hang.
    /// </summary>
    public static async Task<T> AdvanceUntilCompleteAsync<T>(Task<T> task, Action advanceClock)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timer.Elapsed > Limit)
            {
                throw new TimeoutException(
                    $"The task did not finish after {Limit.TotalSeconds:0} seconds of advancing the fake clock. "
                    + "It probably waits on something other than the fake clock.");
            }

            advanceClock();
            await Task.Yield();
        }

        return await task;
    }
}
