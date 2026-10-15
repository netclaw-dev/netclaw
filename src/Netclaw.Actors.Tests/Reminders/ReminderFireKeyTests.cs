// -----------------------------------------------------------------------
// <copyright file="ReminderFireKeyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// The fire key is the text that old journal rows already hold, so the parser must read that text back and
/// refuse anything else. These cases also kill the mutants of <see cref="ReminderFireKey.TryParse"/>
/// (<c>scripts/run-reminder-execution-mutations.sh</c>).
/// </summary>
public sealed class ReminderFireKeyTests
{
    [Theory]
    [InlineData("check-pr", 1_700_000_000_000)]
    [InlineData("a", 0)]
    [InlineData("restart-resume-x7k2", 42)]
    public void A_formatted_key_parses_back_to_the_same_key(string id, long fireTimeMs)
    {
        // Arrange
        var key = new ReminderFireKey(new ReminderId(id), fireTimeMs);

        // Act
        var parsed = ReminderFireKey.TryParse(key.Value, out var result);

        // Assert
        Assert.True(parsed);
        Assert.Equal(key, result);
        Assert.Equal($"{id}:{fireTimeMs}", key.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("alpha")]
    [InlineData(":1")]
    [InlineData("weekly:")]
    [InlineData("weekly:abc")]
    [InlineData("weekly:12x")]
    [InlineData("weekly:-1")]
    [InlineData("weekly:+1")]
    [InlineData("weekly: 1")]
    [InlineData("weekly:1 ")]
    [InlineData("a:b:1")]
    [InlineData("weekly:99999999999999999999")]
    public void Text_that_is_not_a_fire_key_is_refused(string? text)
    {
        // Act
        var parsed = ReminderFireKey.TryParse(text, out var key);

        // Assert
        Assert.False(parsed);
        Assert.Equal(default, key);
    }
}
