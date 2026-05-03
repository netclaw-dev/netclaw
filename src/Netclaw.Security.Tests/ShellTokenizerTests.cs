// -----------------------------------------------------------------------
// <copyright file="ShellTokenizerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellTokenizerTests
{
    // ── Tokenize ──

    [Fact]
    public void Tokenize_splits_simple_command()
    {
        var tokens = ShellTokenizer.Tokenize("git push origin main").ToList();
        Assert.Equal(["git", "push", "origin", "main"], tokens);
    }

    [Fact]
    public void Tokenize_handles_quoted_strings()
    {
        var tokens = ShellTokenizer.Tokenize("git commit -m \"fix the bug\"").ToList();
        Assert.Equal(["git", "commit", "-m", "fix the bug"], tokens);
    }

    [Fact]
    public void Tokenize_handles_single_quotes()
    {
        var tokens = ShellTokenizer.Tokenize("echo 'hello world'").ToList();
        Assert.Equal(["echo", "hello world"], tokens);
    }

    [Fact]
    public void Tokenize_handles_extra_whitespace()
    {
        var tokens = ShellTokenizer.Tokenize("  ls   -la   /tmp  ").ToList();
        Assert.Equal(["ls", "-la", "/tmp"], tokens);
    }

    [Fact]
    public void Tokenize_empty_string_returns_empty()
    {
        var tokens = ShellTokenizer.Tokenize("").ToList();
        Assert.Empty(tokens);
    }

    // ── SplitCompoundCommand ──

    [Fact]
    public void SplitCompound_splits_on_double_ampersand()
    {
        var segments = ShellTokenizer.SplitCompoundCommand("git add . && git commit -m fix");
        Assert.Equal(["git add .", "git commit -m fix"], segments);
    }

    [Fact]
    public void SplitCompound_splits_on_double_pipe()
    {
        var segments = ShellTokenizer.SplitCompoundCommand("test -f foo || echo missing");
        Assert.Equal(["test -f foo", "echo missing"], segments);
    }

    [Fact]
    public void SplitCompound_splits_on_semicolon()
    {
        var segments = ShellTokenizer.SplitCompoundCommand("echo hello; echo world");
        Assert.Equal(["echo hello", "echo world"], segments);
    }

    [Fact]
    public void SplitCompound_splits_on_pipe()
    {
        var segments = ShellTokenizer.SplitCompoundCommand("cat file.txt | grep error");
        Assert.Equal(["cat file.txt", "grep error"], segments);
    }

    [Fact]
    public void SplitCompound_handles_multiple_operators()
    {
        var segments = ShellTokenizer.SplitCompoundCommand("git add . && git commit -m fix && git push");
        Assert.Equal(["git add .", "git commit -m fix", "git push"], segments);
    }

    [Fact]
    public void SplitCompound_preserves_quoted_operators()
    {
        var segments = ShellTokenizer.SplitCompoundCommand("echo \"a && b\" && echo done");
        Assert.Equal(2, segments.Count);
        Assert.Equal("echo \"a && b\"", segments[0]);
        Assert.Equal("echo done", segments[1]);
    }

    [Fact]
    public void SplitCompound_single_command_returns_one_segment()
    {
        var segments = ShellTokenizer.SplitCompoundCommand("git push origin main");
        Assert.Single(segments);
        Assert.Equal("git push origin main", segments[0]);
    }

    [Fact]
    public void SplitCompound_empty_returns_empty()
    {
        var segments = ShellTokenizer.SplitCompoundCommand("");
        Assert.Empty(segments);
    }

    // ── ExtractVerbChain ──

    [Theory]
    [InlineData("git push origin main", "git push")]
    [InlineData("ls -la /tmp", "ls /tmp")]
    [InlineData("docker compose up -d", "docker compose")]
    [InlineData("cat /etc/hosts", "cat /etc/hosts")]
    [InlineData("cat .gitignore", "cat .gitignore")]
    [InlineData("kubectl delete pod my-pod", "kubectl delete")]
    [InlineData("", "")]
    public void ExtractVerbChain_extracts_expected_chain(string input, string expected)
    {
        Assert.Equal(expected, ShellTokenizer.ExtractVerbChain(input));
    }

    [Theory]
    // Path-aware verbs include first non-flag argument
    [InlineData("cat /etc/passwd", "cat /etc/passwd")]
    [InlineData("grep secret /var/log/syslog", "grep secret")]
    [InlineData("bash /home/user/.netclaw/scripts/monitor.sh", "bash /home/user/.netclaw/scripts/monitor.sh")]
    [InlineData("python3 /opt/scripts/report.py --verbose", "python3 /opt/scripts/report.py")]
    [InlineData("curl https://example.com/api", "curl https://example.com/api")]
    [InlineData("find /var/log -name '*.log'", "find /var/log")]
    [InlineData("sed -i 's/foo/bar/' /etc/config.txt", "sed s/foo/bar/")]
    // Structured CLIs unchanged
    [InlineData("git push origin main", "git push")]
    [InlineData("docker compose up -d", "docker compose")]
    [InlineData("kubectl delete pod my-pod", "kubectl delete")]
    [InlineData("dotnet build --configuration Release", "dotnet build")]
    // Edge: flag-only invocations of path-aware verbs
    [InlineData("grep --version", "grep")]
    [InlineData("cat --help", "cat")]
    // Edge: home-relative and env-var paths
    [InlineData("cat ~/.bashrc", "cat ~/.bashrc")]
    [InlineData("bash ~/scripts/deploy.sh", "bash ~/scripts/deploy.sh")]
    public void ExtractVerbChain_path_aware_verbs(string input, string expected)
    {
        Assert.Equal(expected, ShellTokenizer.ExtractVerbChain(input));
    }

    [Fact]
    public void ExtractVerbChain_deeper_with_explicit_max_depth()
    {
        Assert.Equal("docker compose up", ShellTokenizer.ExtractVerbChain("docker compose up -d", maxDepth: 3));
    }

    // ── ExtractInnerCommands ──

    [Theory]
    [InlineData("bash -c \"git push --force\"", "git push --force")]
    [InlineData("sh -c \"rm -rf /tmp/build\"", "rm -rf /tmp/build")]
    public void ExtractInner_shell_c_wrapper_extracts_inner_command(string input, string expectedInner)
    {
        var inner = ShellTokenizer.ExtractInnerCommands(input);
        Assert.Single(inner);
        Assert.Equal(expectedInner, inner[0]);
    }

    [Theory]
    [InlineData("git push origin main")]
    [InlineData("bash script.sh")]
    public void ExtractInner_returns_empty_when_no_wrapper(string input)
    {
        var inner = ShellTokenizer.ExtractInnerCommands(input);
        Assert.Empty(inner);
    }

    // ── GetAllCommandSegments ──

    [Fact]
    public void GetAllSegments_compound_with_inner()
    {
        var segments = ShellTokenizer.GetAllCommandSegments(
            "echo start && bash -c \"git push --force\"");
        // Should include: "echo start", "bash -c \"git push --force\"", and the inner "git push --force"
        Assert.Contains("echo start", segments);
        Assert.Contains("git push --force", segments);
    }

    [Fact]
    public void GetAllSegments_simple_command()
    {
        var segments = ShellTokenizer.GetAllCommandSegments("git status");
        Assert.Single(segments);
        Assert.Equal("git status", segments[0]);
    }

    // ── LooksLikePath ──

    // Anchored paths — always true
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/home/user/.netclaw/workspaces/project/file.txt")]
    [InlineData("./script.sh")]
    [InlineData("../parent/config.json")]
    [InlineData("~/Documents/notes.md")]
    [InlineData("~")]
    [InlineData("$HOME/.config/app.toml")]
    [InlineData("${HOME}/workspace")]
    [InlineData("C:\\Users\\file.txt")]
    [InlineData("c:\\users\\documents")]
    [InlineData("D:/Projects/src")]
    [InlineData("C:/Windows/System32")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("\\\\nas\\backups")]
    public void LooksLikePath_anchored_paths(string token)
    {
        Assert.True(ShellTokenizer.LooksLikePath(token));
    }

    // Non-paths — always false
    [Theory]
    [InlineData("https://api.github.com/repos/foo")]
    [InlineData("ftp://mirror.example.com/file")]
    [InlineData("--output=/tmp/foo")]
    [InlineData("-v")]
    [InlineData("origin/main")]
    [InlineData("feature/fix-bug")]
    [InlineData("nginx:latest")]
    [InlineData("ghcr.io/org/image:tag")]
    [InlineData("redis:6379")]
    [InlineData("@scope/package")]
    [InlineData("s/foo/bar/g")]
    [InlineData("y/abc/xyz/")]
    [InlineData("application/json")]
    [InlineData("git")]
    [InlineData("status")]
    [InlineData("TODO")]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikePath_non_paths(string token)
    {
        Assert.False(ShellTokenizer.LooksLikePath(token));
    }

    // Bare relative with file extension — treated as path
    [Theory]
    [InlineData("src/main.rs")]
    [InlineData("config/app.json")]
    [InlineData("logs/output.log")]
    [InlineData("scripts/deploy.sh")]
    public void LooksLikePath_relative_with_extension(string token)
    {
        Assert.True(ShellTokenizer.LooksLikePath(token));
    }

    // Path traversal in unanchored token — treated as path
    [Theory]
    [InlineData("foo/../bar")]
    [InlineData("workspace/project/../other/file.txt")]
    public void LooksLikePath_traversal_component(string token)
    {
        Assert.True(ShellTokenizer.LooksLikePath(token));
    }

    // Backslash always indicates Windows path
    [Theory]
    [InlineData("src\\main.cs")]
    [InlineData("folder\\subfolder")]
    public void LooksLikePath_backslash(string token)
    {
        Assert.True(ShellTokenizer.LooksLikePath(token));
    }
}
