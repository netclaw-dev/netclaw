// -----------------------------------------------------------------------
// <copyright file="LogSafeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class LogSafeTests
{
    [Theory]
    // A forged entry may only appear as a new line, so every line terminator must go.
    [InlineData("ok\nlevel=critical", "oklevel=critical")]
    [InlineData("ok\r\nlevel=critical", "oklevel=critical")]
    [InlineData("ok\rlevel=critical", "oklevel=critical")]
    [InlineData("ok\u2028level=critical", "oklevel=critical")]
    [InlineData("ok\u2029level=critical", "oklevel=critical")]
    [InlineData("ok\0null", "oknull")]
    [InlineData("ok\u001bescape", "okescape")]
    public void Text_StripsEveryLineTerminator(string input, string expected)
    {
        Assert.Equal(expected, LogSafe.Text(input));
    }

    [Fact]
    public void Text_StripsRatherThanReplacingWithSpace()
    {
        // A space would present the payload as a readable field separator to a key=value
        // parser, which is the same forgery with extra steps.
        var result = LogSafe.Text("reason=ok\nlevel=critical");

        Assert.DoesNotContain(' ', result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Text_NullOrEmpty_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, LogSafe.Text(input));
    }

    [Fact]
    public void Text_AlreadyCleanValue_IsUnchanged()
    {
        Assert.Equal("/home/petabridge/.netclaw", LogSafe.Text("/home/petabridge/.netclaw"));
    }

    [Fact]
    public void Text_ValueLongerThanLimit_TrimsToLimit()
    {
        var result = LogSafe.Text(new string('a', 500));

        Assert.Equal(LogSafe.DefaultMaxChars, result.Length);
    }

    [Fact]
    public void Text_RespectsExplicitLimit()
    {
        Assert.Equal("abcd", LogSafe.Text("abcdefgh", 4));
    }

    [Fact]
    public void Text_CutInsideSurrogatePair_DropsThePair()
    {
        // "ab" plus an astral character: the high surrogate sits at index 2, so a limit of 3
        // would cut the pair in half. A dangling high surrogate becomes U+FFFD downstream.
        var input = "ab\U0001F600cd";

        var result = LogSafe.Text(input, 3);

        Assert.Equal("ab", result);
    }

    [Fact]
    public void Text_CutAfterCompletePair_KeepsThePair()
    {
        var input = "ab\U0001F600cd";

        Assert.Equal("ab\U0001F600", LogSafe.Text(input, 4));
    }
}
