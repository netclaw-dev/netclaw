// -----------------------------------------------------------------------
// <copyright file="CronScheduleHelperTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Reminders;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

public class CronScheduleHelperTests
{
    [Theory]
    [InlineData("not a cron", false)]
    [InlineData("", false)]
    [InlineData("0 */6 * * *", true)]
    public void TryParse_validates_expressions(string expr, bool expected)
    {
        Assert.Equal(expected, CronScheduleHelper.TryParse(expr));
    }

    [Theory]
    [InlineData("* * * * *")]       // every minute
    [InlineData("0 0 * * *")]       // daily at midnight
    [InlineData("0 */6 * * *")]     // every 6 hours
    [InlineData("30 8 * * 1-5")]    // weekdays at 8:30
    public void TryParse_standard_expressions_return_true(string expr)
    {
        Assert.True(CronScheduleHelper.TryParse(expr));
    }

    [Fact]
    public void GetNextOccurrence_returns_future_time()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var next = CronScheduleHelper.GetNextOccurrence("0 */6 * * *", from);

        Assert.NotNull(next);
        Assert.True(next > from);
    }

    [Fact]
    public void GetNextOccurrence_every_minute_returns_next_minute()
    {
        var from = new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.Zero);
        var next = CronScheduleHelper.GetNextOccurrence("* * * * *", from);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 14, 31, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_daily_midnight_returns_next_day()
    {
        var from = new DateTimeOffset(2026, 3, 5, 0, 0, 1, TimeSpan.Zero);
        var next = CronScheduleHelper.GetNextOccurrence("0 0 * * *", from);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 3, 6, 0, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_with_TimeProvider_uses_current_time()
    {
        var fakeTime = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));

        var next = CronScheduleHelper.GetNextOccurrence("0 */6 * * *", fakeTime);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 18, 0, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData("*/15 * * * *", "every 15 minute(s)")]
    [InlineData("0 */6 * * *", "every 6 hour(s)")]
    [InlineData("0 9 * * *", "daily at 09:00 UTC")]
    [InlineData("0 0 * * *", "daily at 00:00 UTC")]
    [InlineData("0 9 * * MON-FRI", "weekdays at 09:00 UTC")]
    [InlineData("0 9 * * 1-5", "weekdays at 09:00 UTC")]
    [InlineData("0 9 * * SAT,SUN", "weekends at 09:00 UTC")]
    [InlineData("0 9 * * MON", "every Monday at 09:00 UTC")]
    [InlineData("0 14 * * FRI", "every Friday at 14:00 UTC")]
    [InlineData("0 9 1 * *", "monthly on day 1 at 09:00 UTC")]
    [InlineData("0 9 * * MON,WED,FRI", "every Mon, Wed, Fri at 09:00 UTC")]
    public void Describe_translates_common_patterns(string cron, string expected)
    {
        Assert.Equal(expected, CronScheduleHelper.Describe(cron));
    }

    [Fact]
    public void Describe_falls_back_for_complex_expressions()
    {
        // Complex expression that doesn't match simple patterns
        var result = CronScheduleHelper.Describe("0 9 1-15 * MON");
        Assert.StartsWith("cron '", result);
    }

}
