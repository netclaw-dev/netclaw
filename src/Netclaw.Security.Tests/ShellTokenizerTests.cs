// -----------------------------------------------------------------------
// <copyright file="ShellTokenizerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellTokenizerTests
{
    /// <summary>
    /// xunit.v3 <c>SkipUnless</c> hook for tests whose expected output
    /// depends on POSIX <c>Path.GetDirectoryName</c> semantics —
    /// Windows produces backslashed parents that don't match the
    /// forward-slash expectations baked into the inline data.
    /// </summary>
    public static bool IsPosix => !OperatingSystem.IsWindows();


    public static TheoryData<string, bool> WindowsAnchoredPathCases
    {
        get
        {
            var expected = OperatingSystem.IsWindows();
            return new TheoryData<string, bool>
            {
                { @"C:\Users\file.txt", expected },
                { @"c:\users\documents", expected },
                { "D:/Projects/src", expected },
                { "C:/Windows/System32", expected },
                { @"\\server\share\file.txt", expected },
                { @"\\nas\backups", expected }
            };
        }
    }

    public static TheoryData<string, bool> BackslashPathCases
    {
        get
        {
            var expected = OperatingSystem.IsWindows();
            return new TheoryData<string, bool>
            {
                { @"src\main.cs", expected },
                { @"folder\subfolder", expected }
            };
        }
    }

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
    public void SplitCompound_keeps_pipeline_in_same_approval_unit()
    {
        var segments = ShellTokenizer.SplitCompoundCommand("cat file.txt | grep error");
        Assert.Single(segments);
        Assert.Equal("cat file.txt | grep error", segments[0]);
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
        // Avoid trailing "done"/"fi"/"esac" in unquoted positions — the
        // section 3 messy detector flags those as control-flow keywords and
        // SplitCompoundCommand returns empty. The token "finished" is a
        // close stand-in that exercises the same splitter behavior.
        var segments = ShellTokenizer.SplitCompoundCommand("echo \"a && b\" && echo finished");
        Assert.Equal(2, segments.Count);
        Assert.Equal("echo \"a && b\"", segments[0]);
        Assert.Equal("echo finished", segments[1]);
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
    // Greedy extraction: chain extends through every verb-like token
    // (no slash, no dot, no flag prefix) until it hits a path or flag.
    // Multi-token CLI subcommands and arg shapes ride along — that's
    // the intended contract for narrow auto-proposed approval patterns.
    [InlineData("git push origin main", "git push origin main")]
    [InlineData("docker compose up -d", "docker compose up")]
    [InlineData("kubectl delete pod my-pod", "kubectl delete pod my-pod")]
    // Path-aware verbs short-circuit at depth 1 — the first positional
    // arg is a path/search-pattern, not a subcommand.
    [InlineData("ls -la /tmp", "ls")]
    [InlineData("cat /etc/hosts", "cat")]
    [InlineData("cat .gitignore", "cat")]
    [InlineData("", "")]
    public void ExtractVerbChain_extracts_expected_chain(string input, string expected)
    {
        Assert.Equal(expected, ShellTokenizer.ExtractVerbChain(input));
    }

    [Theory]
    // Production regressions for the v2 depth-2 cap (issue #27 follow-on).
    // Pre-fix these surfaced as truncated approval prompts ("Approve
    // freshdesk ticket in ...") and verb-chain mismatches between
    // approval-prompt time and retry time, throwing
    // ToolApprovalRequiredException in flight.
    [InlineData("freshdesk ticket list --status open", "freshdesk ticket list")]
    [InlineData("git worktree list", "git worktree list")]
    [InlineData("gh pr view 123 --json title", "gh pr view")]
    [InlineData("kubectl get pods -n default", "kubectl get pods")]
    public void ExtractVerbChain_extracts_multi_token_cli_subcommands(string input, string expected)
    {
        Assert.Equal(expected, ShellTokenizer.ExtractVerbChain(input));
    }

    [Theory]
    // Verb-only extraction — paths and arguments are separate concerns
    // (see ExtractFirstPathArgument tests). The pre-fix behavior of
    // appending the first arg to the verb chain (e.g.
    // "cat /etc/passwd" → "cat /etc/passwd") was the v2 bug fixed here.
    [InlineData("cat /etc/passwd", "cat")]
    [InlineData("grep secret /var/log/syslog", "grep")]
    [InlineData("bash /home/user/.netclaw/scripts/monitor.sh", "bash")]
    [InlineData("python3 /opt/scripts/report.py --verbose", "python3")]
    [InlineData("curl https://example.com/api", "curl")]
    [InlineData("find /var/log -name '*.log'", "find")]
    [InlineData("sed -i 's/foo/bar/' /etc/config.txt", "sed")]
    // Structured CLIs — greedy through verb-like tokens, halts at flag/path
    [InlineData("git push origin main", "git push origin main")]
    [InlineData("docker compose up -d", "docker compose up")]
    [InlineData("kubectl delete pod my-pod", "kubectl delete pod my-pod")]
    [InlineData("dotnet build --configuration Release", "dotnet build")]
    // Edge: flag-only invocations of path-aware verbs
    [InlineData("grep --version", "grep")]
    [InlineData("cat --help", "cat")]
    // Edge: home-relative paths — verb only, path lives in
    // ExtractFirstPathArgument
    [InlineData("cat ~/.bashrc", "cat")]
    [InlineData("bash ~/scripts/deploy.sh", "bash")]
    public void ExtractVerbChain_verb_only(string input, string expected)
    {
        Assert.Equal(expected, ShellTokenizer.ExtractVerbChain(input));
    }

    [Theory]
    // Single-token command verbs (date, which, uname, ...) have no sub-command
    // grammar — depth-1 capping keeps their call-specific operand (a format
    // string, a lookup target, a flag) out of the verb chain so the bundled
    // safe-verb list can match them by exact equality.
    [InlineData("date +%Y-%m-%d", "date")]
    [InlineData("date", "date")]
    [InlineData("which ilspycmd", "which")]
    [InlineData("id aaron", "id")]
    [InlineData("uname -a", "uname")]
    [InlineData("whoami", "whoami")]
    public void ExtractVerbChain_caps_single_token_command_verbs(string input, string expected)
    {
        Assert.Equal(expected, ShellTokenizer.ExtractVerbChain(input));
    }

    // ── IsPathToken ──

    [Theory]
    [InlineData("/", true)]
    [InlineData("/home/petabridge", true)]
    [InlineData("/etc/hosts", true)]
    [InlineData("~", true)]
    [InlineData("~/", true)]
    [InlineData("~/.bashrc", true)]
    [InlineData(".", true)]
    [InlineData("./", true)]
    [InlineData("./build", true)]
    [InlineData("..", true)]
    [InlineData("../shared", true)]
    // Negative cases — tokens with internal slashes that are NOT paths
    [InlineData("https://example.com/api", false)]
    [InlineData("a/b", false)]
    [InlineData("'a/b'", false)]
    [InlineData("foo/bar:tag", false)]
    [InlineData("origin/main", false)]
    [InlineData("s/foo/bar/", false)]
    [InlineData("--verbose", false)]
    [InlineData("netclaw", false)]
    [InlineData("", false)]
    public void IsPathToken_classifies_correctly(string token, bool expected)
    {
        Assert.Equal(expected, ShellTokenizer.IsPathToken(token));
    }

    // ── ExtractFirstPathArgument ──

    [Theory]
    // Absolute paths
    [InlineData("find /home/petabridge -name X", "/home/petabridge")]
    [InlineData("ls -la /tmp", "/tmp")]
    [InlineData("grep -r foo /var/log", "/var/log")]
    // Tilde-prefixed file → parent directory via file-parent rule
    // (Path.GetDirectoryName returns the parent without a trailing
    // separator; the matcher's under-check is tolerant of both forms.)
    [InlineData("cat ~/.bashrc", "~")]
    [InlineData("cat ~/.profile", "~")]
    // Tilde-prefixed directory (no extension) preserved as-is
    [InlineData("ls ~/repos", "~/repos")]
    // Relative dot/dot-dot paths
    [InlineData("grep -r foo ./build", "./build")]
    [InlineData("ls .", ".")]
    [InlineData("cd ..", "..")]
    // File path without extension stays as-is
    [InlineData("cat /etc/hosts", "/etc/hosts")]
    // No path argument
    [InlineData("git status", null)]
    [InlineData("echo hello", null)]
    [InlineData("freshdesk --since=24h", null)]
    // URL not classified as path
    [InlineData("curl https://example.com/foo", null)]
    // Internal-slash regex literal not classified as path
    [InlineData("grep -r 'a/b' .", ".")]
    public void ExtractFirstPathArgument_returns_first_path_or_null(string command, string? expected)
    {
        Assert.Equal(expected, ShellTokenizer.ExtractFirstPathArgument(command));
    }

    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only Path.GetDirectoryName semantics")]
    // File path with extension → parent. Path.GetDirectoryName is
    // platform-aware: on Windows the parent uses backslashes which
    // doesn't match these forward-slash expectations, so the cases
    // live in a POSIX-gated theory.
    [InlineData("cp /src/a.txt /dst/b.txt", "/src")]
    [InlineData("cat /etc/hosts.conf", "/etc")]
    public void ExtractFirstPathArgument_applies_file_parent_rule_posix(string command, string expected)
    {
        Assert.Equal(expected, ShellTokenizer.ExtractFirstPathArgument(command));
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
    public void LooksLikePath_anchored_paths(string token)
    {
        Assert.True(ShellTokenizer.LooksLikePath(token));
    }

    [Theory]
    [MemberData(nameof(WindowsAnchoredPathCases))]
    public void LooksLikePath_windows_anchored_paths_follow_active_shell_family(string token, bool expected)
    {
        Assert.Equal(expected, ShellTokenizer.LooksLikePath(token));
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
    [MemberData(nameof(BackslashPathCases))]
    public void LooksLikePath_backslash(string token, bool expected)
    {
        Assert.Equal(expected, ShellTokenizer.LooksLikePath(token));
    }

    // ── IsMessyCompoundCommand ──

    [Theory]
    [InlineData("for pid in $(pgrep netclawd); do echo \"$pid\"; done")]
    [InlineData("while read line; do echo $line; done < input.txt")]
    [InlineData("if [ -f x ]; then echo y; fi")]
    [InlineData("case $x in 1) echo one ;; 2) echo two ;; esac")]
    [InlineData("for f in *.log; do grep ERROR \"$f\"; done")]
    public void IsMessyCompoundCommand_flags_bash_control_flow(string command)
    {
        Assert.True(ShellTokenizer.IsMessyCompoundCommand(command));
    }

    [Theory]
    [InlineData("echo \"unterminated")]
    [InlineData("echo 'still open")]
    [InlineData("echo $(unclosed")]
    [InlineData("ls [unclosed")]
    [InlineData("echo too )many close parens")]
    [InlineData("echo too ]many close brackets")]
    public void IsMessyCompoundCommand_flags_unbalanced_quotes_or_brackets(string command)
    {
        Assert.True(ShellTokenizer.IsMessyCompoundCommand(command));
    }

    [Theory]
    [InlineData("git push origin main")]
    [InlineData("grep error /var/log/syslog")]
    [InlineData("git add . && git commit -m fix && git push")]
    [InlineData("cat file.log | grep error | wc -l")]
    [InlineData("echo $(date)")]
    [InlineData("ls ${HOME}")]
    [InlineData("find . -name '*.log' -type f")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMessyCompoundCommand_passes_well_formed_commands(string command)
    {
        Assert.False(ShellTokenizer.IsMessyCompoundCommand(command));
    }

    [Fact]
    public void IsMessyCompoundCommand_does_not_flag_keywords_inside_quotes()
    {
        // A literal "done" inside a quoted string is not a control-flow token.
        Assert.False(ShellTokenizer.IsMessyCompoundCommand("echo \"done\""));
    }

    [Fact]
    public void IsMessyCompoundCommand_does_not_flag_keyword_substrings()
    {
        // "format" contains "for" but is not the for-loop opener.
        Assert.False(ShellTokenizer.IsMessyCompoundCommand("python format.py"));
        // "fido" contains "fi" but is not the if-block closer.
        Assert.False(ShellTokenizer.IsMessyCompoundCommand("echo fido"));
    }

    [Theory]
    [InlineData("for pid in $(pgrep netclawd); do echo \"$pid\"; done")]
    [InlineData("while read line; do echo $line; done")]
    [InlineData("if [ -f x ]; then echo y; fi")]
    public void SplitCompoundCommand_returns_empty_for_messy_input(string command)
    {
        Assert.Empty(ShellTokenizer.SplitCompoundCommand(command));
    }

    [Fact]
    public void SplitCompoundCommand_still_splits_well_formed_compounds()
    {
        var segments = ShellTokenizer.SplitCompoundCommand("git add . && git push");
        Assert.Equal(2, segments.Count);
    }
}
