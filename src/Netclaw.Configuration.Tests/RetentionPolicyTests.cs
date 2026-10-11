// -----------------------------------------------------------------------
// <copyright file="RetentionPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class RetentionPolicyTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("14", 14)]
    [InlineData(" 30 ", 30)]
    [InlineData("36500", 36500)]
    public void TryParseDays_accepts_whole_numbers_in_range(string text, int expected)
    {
        Assert.True(RetentionPolicy.TryParseDays(text, out var days, out var error));
        Assert.Equal(expected, days);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("36501")]
    [InlineData("99999999999")]
    public void TryParseDays_rejects_everything_else(string? text)
    {
        Assert.False(RetentionPolicy.TryParseDays(text, out _, out var error));
        Assert.Contains("0 to 36500", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_setting_has_a_distinct_key_and_option_and_a_default_the_parser_accepts()
    {
        Assert.Equal(RetentionSettings.All.Count, RetentionSettings.All.Select(static s => s.ConfigKey).Distinct().Count());
        Assert.Equal(RetentionSettings.All.Count, RetentionSettings.All.Select(static s => s.CliOption).Distinct().Count());
        Assert.All(RetentionSettings.All, s => Assert.True(RetentionPolicy.TryParseDays(s.DefaultDays.ToString(), out _, out _)));
    }
}
