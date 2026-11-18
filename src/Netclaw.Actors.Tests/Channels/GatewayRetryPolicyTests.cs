// -----------------------------------------------------------------------
// <copyright file="GatewayRetryPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class GatewayRetryPolicyTests
{
    [Theory]
    [InlineData(0, 0, 5)]
    [InlineData(5, 5, 10)]
    [InlineData(10, 10, 20)]
    [InlineData(150, 150, 300)]
    [InlineData(300, 300, 300)]
    [InlineData(301, 301, 300)]
    public void Schedule_uses_current_delay_and_caps_the_next_delay(
        int currentSeconds, int dueSeconds, int nextSeconds)
    {
        var schedule = GatewayRetryPolicy.NextSchedule(TimeSpan.FromSeconds(currentSeconds));

        Assert.Equal(TimeSpan.FromSeconds(dueSeconds), schedule.DueAfter);
        Assert.Equal(TimeSpan.FromSeconds(nextSeconds), schedule.NextDelay);
    }

    [Fact]
    public void Repeated_failures_grow_the_due_time()
    {
        var delay = TimeSpan.Zero;
        var dueTimes = new List<TimeSpan>();
        for (var i = 0; i < 4; i++)
        {
            var schedule = GatewayRetryPolicy.NextSchedule(delay);
            dueTimes.Add(schedule.DueAfter);
            delay = schedule.NextDelay;
        }

        Assert.Equal(
            new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) },
            dueTimes);
    }

    [Theory]
    [InlineData(-1000, 10)]
    [InlineData(0, 10)]
    [InlineData(59999, 10)]
    [InlineData(60000, 0)]
    [InlineData(61000, 0)]
    public void Stable_ready_time_resets_the_retry_delay(int readyAgeMilliseconds, int expectedSeconds)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var enteredAt = now - TimeSpan.FromMilliseconds(readyAgeMilliseconds);

        var delay = GatewayRetryPolicy.DelayAfterReady(TimeSpan.FromSeconds(10), enteredAt, now);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }
}
