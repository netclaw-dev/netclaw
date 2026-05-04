// -----------------------------------------------------------------------
// <copyright file="ShellApprovalMatcherTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellApprovalMatcherTests
{
    private readonly ShellApprovalMatcher _matcher = ShellApprovalMatcher.Instance;

    private static Dictionary<string, object?> Args(string command) => new() { ["Command"] = command };

    [Fact]
    public void ExtractPatterns_simple_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("git push origin main"));
        Assert.Single(patterns);
        Assert.Equal("git push", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_compound_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"));
        Assert.Equal(3, patterns.Count);
        Assert.Contains("git add", patterns);
        Assert.Contains("git commit", patterns);
        Assert.Contains("git push", patterns);
    }

    [Fact]
    public void ExtractPatterns_deduplicates()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("git push && git push --tags"));
        // Both segments produce "git push", should be deduplicated
        Assert.Single(patterns);
        Assert.Equal("git push", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_recurses_into_bash_c_wrapper()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("bash -c \"git push --force\""));

        Assert.Single(patterns);
        Assert.Equal("git push", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_batches_outer_and_inner_segments()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("echo ok && bash -c \"git push --force\""));

        Assert.Equal(2, patterns.Count);
        Assert.Contains("echo ok", patterns);
        Assert.Contains("git push", patterns);
    }

    [Fact]
    public void ExtractPatterns_empty_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args(""));
        Assert.Empty(patterns);
    }

    [Fact]
    public void IsApproved_all_patterns_approved()
    {
        var approved = new[] { "git add", "git commit", "git push" };
        Assert.True(_matcher.IsApproved(new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"), approved));
    }

    [Fact]
    public void IsApproved_one_pattern_unapproved()
    {
        var approved = new[] { "git add", "git push" };
        Assert.False(_matcher.IsApproved(new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"), approved));
    }

    [Theory]
    [InlineData("gh", "gh --help", true)]        // Single-token exact match
    [InlineData("gh", "gh pr create", false)]    // Single-token should NOT prefix match
    [InlineData("git push", "git push origin main", true)]  // Multi-token prefix match
    [InlineData("git push", "git pull", false)]  // Multi-token no match
    [InlineData("git pu", "git push", false)]    // Partial token no match (word boundary)
    [InlineData("gh pr", "gh pr create", true)]  // Multi-token prefix match
    [InlineData("gh pr", "gh issue list", false)] // Multi-token no match
    // Path-aware patterns — exact path matches
    [InlineData("cat /etc/passwd", "cat /etc/passwd", true)]
    [InlineData("cat /etc/passwd", "cat /etc/shadow", false)]
    [InlineData("bash /home/.netclaw/scripts/monitor.sh", "bash /home/.netclaw/scripts/monitor.sh", true)]
    [InlineData("bash /home/.netclaw/scripts/monitor.sh", "bash /tmp/evil.sh", false)]
    // Single-token path-aware verbs stay exact-only
    [InlineData("cat", "cat /etc/passwd", false)]
    [InlineData("grep", "grep TODO", false)]
    [InlineData("bash", "bash /tmp/script.sh", false)]
    [InlineData("find", "find /var/log", false)]
    // Non-path-aware single tokens still require exact match
    [InlineData("echo", "echo hello", false)]
    [InlineData("docker", "docker compose", false)]
    public void IsApproved_pattern_matching(string pattern, string command, bool expected)
    {
        var approved = new[] { pattern };
        Assert.Equal(expected, _matcher.IsApproved(new ToolName("shell_execute"), Args(command), approved));
    }

    [Fact]
    public void IsApproved_recurses_into_bash_c_wrapper()
    {
        var approved = new[] { "git push" };

        Assert.True(_matcher.IsApproved(new ToolName("shell_execute"),
            Args("bash -c \"git push --force\""), approved));
    }

    [Fact]
    public void ExtractPatterns_path_aware_verb_includes_path()
    {
        var patterns = _matcher.ExtractPatterns(
            new ToolName("shell_execute"),
            Args("cat /etc/hosts && git push origin main"));
        Assert.Equal(2, patterns.Count);
        Assert.Contains("cat /etc/hosts", patterns);
        Assert.Contains("git push", patterns);
    }

    [Fact]
    public void ExtractPatterns_pipe_with_path_aware_verbs()
    {
        var patterns = _matcher.ExtractPatterns(
            new ToolName("shell_execute"),
            Args("cat /var/log/syslog | grep error"));
        Assert.Equal(2, patterns.Count);
        Assert.Contains("cat /var/log/syslog", patterns);
        Assert.Contains("grep error", patterns);
    }

    [Fact]
    public void FormatForDisplay_returns_command()
    {
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"), Args("git push origin main"));
        Assert.Equal("git push origin main", display);
    }
}

public sealed class DefaultApprovalMatcherTests
{
    private readonly DefaultApprovalMatcher _matcher = DefaultApprovalMatcher.Instance;

    [Fact]
    public void ExtractPatterns_returns_tool_name()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("mcp:memorizer:store"), null);
        Assert.Single(patterns);
        Assert.Equal("mcp:memorizer:store", patterns[0]);
    }

    [Fact]
    public void IsApproved_matches_exact_tool_name()
    {
        Assert.True(_matcher.IsApproved(new ToolName("mcp:memorizer:store"), null, ["mcp:memorizer:store"]));
    }

    [Fact]
    public void IsApproved_no_match()
    {
        Assert.False(_matcher.IsApproved(new ToolName("mcp:memorizer:store"), null, ["mcp:memorizer:get"]));
    }
}
