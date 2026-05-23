// -----------------------------------------------------------------------
// <copyright file="LlmFacingToolNameTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class LlmFacingToolNameTests
{
    [Theory]
    [InlineData("shell_execute", "shell_execute")]
    [InlineData("file_read", "file_read")]
    [InlineData("notion/notion-create-pages", "notion__notion-create-pages")]
    [InlineData("memorizer/store", "memorizer__store")]
    public void FromCanonical_replaces_slash_with_double_underscore(string canonical, string expectedLlmFacing)
    {
        var llm = LlmFacingToolName.FromCanonical(canonical);
        Assert.Equal(expectedLlmFacing, llm.Value);
    }

    [Fact]
    public void FromCanonical_is_idempotent_for_already_safe_names()
    {
        var once = LlmFacingToolName.FromCanonical("shell_execute");
        var twice = LlmFacingToolName.FromCanonical(once.Value);
        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has.dot")]
    [InlineData("has:colon")]
    [InlineData("has\\backslash")]
    [InlineData("emoji_💥")]
    public void FromCanonical_throws_for_names_with_other_disallowed_chars(string canonical)
    {
        Assert.Throws<ArgumentException>(() => LlmFacingToolName.FromCanonical(canonical));
    }

    [Fact]
    public void FromCanonical_throws_for_names_exceeding_128_chars_after_sanitization()
    {
        // 65 chars + '/' + 64 chars = 130 sanitized chars.
        var tooLong = new string('a', 65) + "/" + new string('b', 64);
        Assert.Throws<ArgumentException>(() => LlmFacingToolName.FromCanonical(tooLong));
    }

    [Theory]
    [InlineData("notion__create-pages", "notion/create-pages")]
    [InlineData("memorizer__store", "memorizer/store")]
    [InlineData("browser_chrome_devtools__navigate_page", "browser_chrome_devtools/navigate_page")]
    public void TryReverseSanitizedToCanonical_reverses_first_double_underscore(string llm, string canonical)
    {
        Assert.Equal(canonical, LlmFacingToolName.TryReverseSanitizedToCanonical(llm));
    }

    [Theory]
    [InlineData("shell_execute")]         // first-party — no separator
    [InlineData("file_read")]             // first-party — single underscores
    [InlineData("notion/create-pages")]   // already canonical
    [InlineData("")]                       // empty
    public void TryReverseSanitizedToCanonical_returns_null_for_non_aliases(string name)
    {
        Assert.Null(LlmFacingToolName.TryReverseSanitizedToCanonical(name));
    }

    [Fact]
    public void TryReverseSanitizedToCanonical_returns_null_for_leading_or_trailing_separator()
    {
        // `__foo` (no server prefix) and `foo__` (no tool suffix) are
        // malformed; reverse-resolution returns null so callers don't
        // build invalid canonical names.
        Assert.Null(LlmFacingToolName.TryReverseSanitizedToCanonical("__foo"));
        Assert.Null(LlmFacingToolName.TryReverseSanitizedToCanonical("foo__"));
    }
}
