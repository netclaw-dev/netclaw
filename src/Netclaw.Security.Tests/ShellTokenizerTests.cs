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
    [InlineData("ls -la /tmp", "ls")]
    [InlineData("docker compose up -d", "docker compose")]
    [InlineData("cat /etc/hosts", "cat")]
    [InlineData("cat .gitignore", "cat")]
    [InlineData("kubectl delete pod my-pod", "kubectl delete")]
    [InlineData("", "")]
    public void ExtractVerbChain_extracts_expected_chain(string input, string expected)
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
}
