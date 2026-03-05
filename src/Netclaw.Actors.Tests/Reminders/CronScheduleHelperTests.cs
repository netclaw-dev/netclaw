using Netclaw.Actors.Reminders;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

public class CronScheduleHelperTests
{
    [Fact]
    public void TryParse_valid_expression_returns_true()
    {
        Assert.True(CronScheduleHelper.TryParse("0 */6 * * *"));
    }

    [Fact]
    public void TryParse_invalid_expression_returns_false()
    {
        Assert.False(CronScheduleHelper.TryParse("not a cron"));
    }

    [Fact]
    public void TryParse_empty_string_returns_false()
    {
        Assert.False(CronScheduleHelper.TryParse(""));
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

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
