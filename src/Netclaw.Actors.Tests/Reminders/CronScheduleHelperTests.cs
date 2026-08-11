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

    // ── CRON_TZ time zone prefix ──

    [Theory]
    [InlineData("CRON_TZ=Europe/Brussels 0 9 * * *", true)]
    [InlineData("cron_tz=Europe/Brussels 0 9 * * *", true)] // case-insensitive prefix
    [InlineData("CRON_TZ=UTC 0 9 * * *", true)]
    [InlineData("CRON_TZ=Europe/Brussels", false)] // no expression after prefix
    [InlineData("CRON_TZ= 0 9 * * *", false)] // empty zone id
    [InlineData("CRON_TZ=Not/AZone 0 9 * * *", false)] // unknown zone
    [InlineData("CRON_TZ=Eastern Standard Time 0 9 * * *", false)] // Windows names contain spaces; IANA only
    [InlineData("CRON_TZ=Europe/Brussels not a cron", false)]
    public void TryParse_validates_cron_tz_prefix(string expr, bool expected)
    {
        Assert.Equal(expected, CronScheduleHelper.TryParse(expr));
    }

    [Fact]
    public void TryParse_resolves_cron_tz_time_zone()
    {
        Assert.True(CronScheduleHelper.TryParse("CRON_TZ=Europe/Brussels 0 9 * * *", out var zone));
        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels").Id, zone.Id);

        Assert.True(CronScheduleHelper.TryParse("0 9 * * *", out var utc));
        Assert.Equal(TimeZoneInfo.Utc, utc);
    }

    [Fact]
    public void GetNextOccurrence_evaluates_cron_tz_in_local_zone()
    {
        // 09:00 CEST (UTC+2) on 2026-08-06 is 07:00 UTC
        var from = new DateTimeOffset(2026, 8, 6, 8, 0, 0, TimeSpan.Zero); // 10:00 local, past today's 09:00
        var next = CronScheduleHelper.GetNextOccurrence("CRON_TZ=Europe/Brussels 0 9 * * *", from);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 8, 7, 7, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_cron_tz_handles_dst_spring_forward()
    {
        // Europe/Brussels springs forward on 2026-03-29 02:00 -> 03:00 (CET +1 -> CEST +2).
        // 09:00 on 2026-03-28 is 08:00 UTC; on 2026-03-30 it is 07:00 UTC.
        var before = new DateTimeOffset(2026, 3, 27, 12, 0, 0, TimeSpan.Zero);
        var first = CronScheduleHelper.GetNextOccurrence("CRON_TZ=Europe/Brussels 0 9 * * *", before);
        Assert.Equal(new DateTimeOffset(2026, 3, 28, 8, 0, 0, TimeSpan.Zero), first);

        var after = new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero);
        var next = CronScheduleHelper.GetNextOccurrence("CRON_TZ=Europe/Brussels 0 9 * * *", after);
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 7, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_cron_tz_handles_dst_fall_back()
    {
        // Europe/Brussels falls back on 2026-10-25 03:00 -> 02:00 (CEST +2 -> CET +1).
        // 09:00 on 2026-10-24 is 07:00 UTC; on 2026-10-26 it is 08:00 UTC.
        var before = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var first = CronScheduleHelper.GetNextOccurrence("CRON_TZ=Europe/Brussels 0 9 * * *", before);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero), first);

        var after = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var next = CronScheduleHelper.GetNextOccurrence("CRON_TZ=Europe/Brussels 0 9 * * *", after);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_unknown_cron_tz_zone_throws()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ex = Assert.Throws<Cronos.CronFormatException>(
            () => CronScheduleHelper.GetNextOccurrence("CRON_TZ=Not/AZone 0 9 * * *", from));
        Assert.Contains("Not/AZone", ex.Message);
        Assert.Contains("IANA", ex.Message);
    }

    [Fact]
    public void GetNextOccurrence_windows_style_cron_tz_zone_throws_with_iana_guidance()
    {
        // Windows display names contain spaces; the zone id ends at the first space,
        // so 'Eastern Standard Time' truncates to unknown zone 'Eastern'.
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ex = Assert.Throws<Cronos.CronFormatException>(
            () => CronScheduleHelper.GetNextOccurrence("CRON_TZ=Eastern Standard Time 0 9 * * *", from));
        Assert.Contains("Eastern", ex.Message);
        Assert.Contains("IANA", ex.Message);
    }

    [Fact]
    public void GetNextOccurrence_without_prefix_still_utc()
    {
        // Regression: plain expressions keep UTC semantics
        var from = new DateTimeOffset(2026, 8, 6, 8, 0, 0, TimeSpan.Zero);
        var next = CronScheduleHelper.GetNextOccurrence("0 9 * * *", from);
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData("CRON_TZ=Europe/Brussels 0 9 * * *", "daily at 09:00 Europe/Brussels")]
    [InlineData("CRON_TZ=America/New_York 0 9 * * MON-FRI", "weekdays at 09:00 America/New_York")]
    [InlineData("CRON_TZ=UTC 0 9 * * *", "daily at 09:00 UTC")]
    [InlineData("CRON_TZ=Europe/Brussels */15 * * * *", "every 15 minute(s)")]
    public void Describe_reports_cron_tz_zone(string cron, string expected)
    {
        Assert.Equal(expected, CronScheduleHelper.Describe(cron));
    }

    [Fact]
    public void Describe_falls_back_for_invalid_cron_tz_zone()
    {
        var result = CronScheduleHelper.Describe("CRON_TZ=Not/AZone 0 9 * * *");
        Assert.Equal("CRON_TZ=Not/AZone 0 9 * * *", result);
    }

}
