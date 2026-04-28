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

    [Fact]
    public void ExtractVerbChain_simple_command()
    {
        Assert.Equal("git push", ShellTokenizer.ExtractVerbChain("git push origin main"));
    }

    [Fact]
    public void ExtractVerbChain_stops_at_flag()
    {
        Assert.Equal("ls", ShellTokenizer.ExtractVerbChain("ls -la /tmp"));
    }

    [Fact]
    public void ExtractVerbChain_multi_level_capped_at_default_depth()
    {
        // Default maxDepth=2 captures command + subcommand
        Assert.Equal("docker compose", ShellTokenizer.ExtractVerbChain("docker compose up -d"));
    }

    [Fact]
    public void ExtractVerbChain_deeper_with_explicit_max_depth()
    {
        Assert.Equal("docker compose up", ShellTokenizer.ExtractVerbChain("docker compose up -d", maxDepth: 3));
    }

    [Fact]
    public void ExtractVerbChain_stops_at_path()
    {
        Assert.Equal("cat", ShellTokenizer.ExtractVerbChain("cat /etc/hosts"));
    }

    [Fact]
    public void ExtractVerbChain_stops_at_dotfile()
    {
        Assert.Equal("cat", ShellTokenizer.ExtractVerbChain("cat .gitignore"));
    }

    [Fact]
    public void ExtractVerbChain_kubectl_subcommand()
    {
        // "pod" is a positional arg but maxDepth=2 stops before it
        Assert.Equal("kubectl delete", ShellTokenizer.ExtractVerbChain("kubectl delete pod my-pod"));
    }

    [Fact]
    public void ExtractVerbChain_empty_command()
    {
        Assert.Equal("", ShellTokenizer.ExtractVerbChain(""));
    }

    // ── ExtractInnerCommands ──

    [Fact]
    public void ExtractInner_bash_c_wrapper()
    {
        var inner = ShellTokenizer.ExtractInnerCommands("bash -c \"git push --force\"");
        Assert.Single(inner);
        Assert.Equal("git push --force", inner[0]);
    }

    [Fact]
    public void ExtractInner_sh_c_wrapper()
    {
        var inner = ShellTokenizer.ExtractInnerCommands("sh -c \"rm -rf /tmp/build\"");
        Assert.Single(inner);
        Assert.Equal("rm -rf /tmp/build", inner[0]);
    }

    [Fact]
    public void ExtractInner_no_wrapper()
    {
        var inner = ShellTokenizer.ExtractInnerCommands("git push origin main");
        Assert.Empty(inner);
    }

    [Fact]
    public void ExtractInner_bash_without_c_flag()
    {
        var inner = ShellTokenizer.ExtractInnerCommands("bash script.sh");
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
