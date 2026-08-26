// -----------------------------------------------------------------------
// <copyright file="ShellApprovalMatcherMultilineTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Multi-line shell command coverage for <see cref="ShellApprovalMatcher"/>.
/// A bare newline separates Bash statements. The matcher returns one approval
/// unit for each statement and keeps pipeline stages in one unit.
/// </summary>
public sealed class ShellApprovalMatcherMultilineTests
{
    private readonly ShellApprovalMatcher _matcher = new(
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));

    private static Dictionary<string, object?> Args(string command) => new() { ["Command"] = command };

    /// <summary>
    /// xunit.v3 <c>SkipUnless</c> hook for tests that require POSIX paths and
    /// filesystem behavior. Native PowerShell cases have a separate matrix.
    /// </summary>
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_multiline_command_splits_one_unit_per_statement()
    {
        // A bare newline separates statements, so a multi-line command
        // yields one approval unit per statement rather than a single unit
        // with the newline collapsed to a space.
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("git fetch\ngit status"));

        Assert.Equal(2, patterns.Count);
        Assert.Contains("git fetch", patterns);
        Assert.Contains("git status", patterns);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_multiline_collapses_blank_lines()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("echo a\n\n\necho b"));

        Assert.Equal(2, patterns.Count);
        Assert.Contains("echo a", patterns);
        Assert.Contains("echo b", patterns);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only — matcher routes through BashParser on POSIX")]
    public void ExtractPatterns_multiline_keeps_pipe_within_a_statement()
    {
        // A pipe stays inside one approval unit; the newline still splits
        // the second statement out.
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("echo one | grep o\necho two"));

        Assert.Equal(2, patterns.Count);
        Assert.Contains("echo one | grep o", patterns);
        Assert.Contains("echo two", patterns);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_multiline_surfaces_each_verb()
    {
        // Security-relevant: a verb after a bare newline (here `rm -rf`)
        // must surface as its own gated candidate, not be absorbed as args
        // of the preceding `cd`.
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("cd /tmp\nrm -rf /tmp/foo"));

        Assert.Contains(candidates, c => c.Verb == "cd" && c.Directory == "/tmp");
        Assert.Contains(candidates, c => c.Verb == "rm" && c.Directory == "/tmp/foo");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_uncertain_cwd_without_absolute_scope_fails_closed()
    {
        // A newline runs git even if cd fails. Its cwd can therefore be the
        // original directory or /tmp, and no persistent scope is safe.
        var arguments = Args("cd /tmp\ngit status");

        Assert.Empty(_matcher.ExtractCandidates(new ToolName("shell_execute"), arguments));
        Assert.True(_matcher.IsMessy(new ToolName("shell_execute"), arguments));
    }
}
