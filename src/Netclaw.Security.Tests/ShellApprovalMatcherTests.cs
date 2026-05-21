// -----------------------------------------------------------------------
// <copyright file="ShellApprovalMatcherTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellApprovalMatcherTests
{
    private readonly ShellApprovalMatcher _matcher = ShellApprovalMatcher.Instance;

    private static Dictionary<string, object?> Args(string command) => new() { ["Command"] = command };

    private static Dictionary<string, object?> Args(string command, string workingDirectory)
        => new()
        {
            ["Command"] = command,
            ["WorkingDirectory"] = workingDirectory
        };

    private static ApprovalEntry Verb(string verb) => new(verb) { Directory = null };
    private static ApprovalEntry InDir(string verb, string dir) => new(verb) { Directory = dir };

    [Fact]
    public void ExtractPatterns_simple_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("git push origin main"));
        Assert.Single(patterns);
        Assert.Equal("git push origin main", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_compound_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"));
        Assert.Equal(3, patterns.Count);
        Assert.Contains("git add .", patterns);
        Assert.Contains("git commit -m fix", patterns);
        Assert.Contains("git push", patterns);
    }

    [Fact]
    public void ExtractPatterns_recurses_into_bash_c_wrapper()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("bash -c \"git push --force\""));

        Assert.Single(patterns);
        Assert.Equal("git push --force", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_empty_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args(""));
        Assert.Empty(patterns);
    }

    [Fact]
    public void ExtractCandidateVerbs_collapses_to_verb_chains_only()
    {
        // Pure verb chains, no normalized commands or directory roots — the
        // v2 matcher leaves the directory half of approval pairs to the cwd.
        var verbs = _matcher.ExtractCandidateVerbs(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"));
        Assert.Equal(3, verbs.Count);
        Assert.Contains("git add", verbs);
        Assert.Contains("git commit", verbs);
        Assert.Contains("git push", verbs);
    }

    [Fact]
    public void ExtractCandidateVerbs_emits_command_head_only()
    {
        // v2.1 path-extraction: verb chain is the command head only.
        // The path argument is captured separately on
        // ExtractCandidates(...).Directory; see
        // ShellApprovalMatcherPathExtractionTests for the full coverage.
        var verbs = _matcher.ExtractCandidateVerbs(
            new ToolName("shell_execute"),
            Args("cat /home/user/.netclaw/logs/crash.log"));
        Assert.Single(verbs);
        Assert.Equal("cat", verbs[0]);
    }

    [Fact]
    public void IsApproved_global_wildcard_matches_anywhere()
    {
        var approved = new[] { Verb("git push"), Verb("git add"), Verb("git commit") };
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"),
            approved,
            cwd: "/anywhere"));
    }

    [Fact]
    public void IsApproved_one_verb_unapproved_returns_false()
    {
        var approved = new[] { Verb("git add"), Verb("git push") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"),
            approved,
            cwd: null));
    }

    [Fact]
    public void IsApproved_folder_scoped_entry_matches_when_cwd_is_under_directory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(tempRoot, "sub");
        Directory.CreateDirectory(sub);
        try
        {
            // Use a non-path-aware verb so the candidate stays a pure verb
            // chain ("git status"); path-aware verbs (cat, grep, etc.) append
            // their first positional argument which would not match a bare
            // verb in the approved entry.
            var approved = new[] { InDir("git status", tempRoot) };
            Assert.True(_matcher.IsApproved(
                new ToolName("shell_execute"),
                Args("git status"),
                approved,
                cwd: sub));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IsApproved_folder_scoped_entry_does_not_match_when_cwd_is_outside()
    {
        var approved = new[] { InDir("grep", "/home/user/repos/foo") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("grep error file.log"),
            approved,
            cwd: "/etc"));
    }

    [Fact]
    public void IsApproved_folder_scoped_entry_requires_concrete_cwd()
    {
        var approved = new[] { InDir("grep", "/home/user/repos/foo") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("grep error file.log"),
            approved,
            cwd: null));
    }

    [Fact]
    public void IsApproved_recurses_into_bash_c_wrapper()
    {
        var approved = new[] { Verb("git push") };
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("bash -c \"git push --force\""),
            approved,
            cwd: null));
    }

    [Fact]
    public void FormatForDisplay_returns_command()
    {
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"), Args("git push origin main"));
        Assert.Equal("git push origin main", display);
    }

    [Fact]
    public void IsMessy_true_for_bash_control_flow()
    {
        Assert.True(_matcher.IsMessy(
            new ToolName("shell_execute"),
            Args("for pid in $(pgrep netclawd); do echo $pid; done")));
    }

    [Fact]
    public void IsMessy_false_for_well_formed_compound()
    {
        Assert.False(_matcher.IsMessy(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push")));
    }

    [Fact]
    public void IsApproved_returns_false_for_messy_command_even_with_global_wildcards()
    {
        // Even if every conceivable verb is approved, a messy command never
        // auto-runs: the matcher cannot extract verb chains to evaluate, and
        // the prompt must offer Once/Deny only.
        var approved = new[] { Verb("for"), Verb("do"), Verb("done"), Verb("echo") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("for x in 1 2 3; do echo $x; done"),
            approved,
            cwd: null));
    }
}

/// <summary>
/// Path-extraction-aware matcher tests. The v2.1 design moves path arguments
/// out of the verb chain and into the candidate's directory half so future
/// calls in the same tree match a single persisted entry.
/// </summary>
public sealed class ShellApprovalMatcherPathExtractionTests
{
    private readonly ShellApprovalMatcher _matcher = ShellApprovalMatcher.Instance;

    private static Dictionary<string, object?> Args(string command) => new() { ["Command"] = command };

    /// <summary>
    /// xunit.v3 <c>SkipUnless</c> hook for POSIX-only tests. The v2
    /// matcher falls through to the legacy <c>ShellTokenizer</c> path
    /// on Windows (ShellSyntaxTree is bash-only), so tests that pin
    /// BashParser cwd attribution / <c>arg.Resolved</c> canonicalization
    /// don't apply. Marking them <c>[Fact(SkipUnless = nameof(IsPosix))]</c>
    /// produces a proper "Skipped" entry in the test log on Windows
    /// runners instead of hiding the gap behind an early-return.
    /// </summary>
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_strips_path_from_verb()
    {
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("find /home/user -name X"));

        var c = Assert.Single(candidates);
        Assert.Equal("find", c.Verb);
        Assert.Equal("/home/user", c.Directory);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_applies_file_parent_rule()
    {
        // `cat ~/.bashrc` → directory is the basename's parent (the home
        // directory itself). The matcher canonicalizes via the parser's
        // Resolved field, so the result is the absolute home directory
        // rather than the raw `~` prefix — needed so this candidate
        // compares string-equal with cwd-attributed directories from
        // other clauses in a compound (see the
        // ExtractCandidates_normalizes_tilde_cd... test).
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("cat ~/.bashrc"));

        var c = Assert.Single(candidates);
        Assert.Equal("cat", c.Verb);
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            c.Directory);
    }

    [Fact]
    public void ExtractCandidates_no_path_returns_null_directory()
    {
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("git status"));

        var c = Assert.Single(candidates);
        Assert.Equal("git status", c.Verb);
        Assert.Null(c.Directory);
    }

    [Fact]
    public void ExtractCandidates_compound_command_extracts_per_clause()
    {
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("ls /repo && git status"));

        Assert.Equal(2, candidates.Count);
        Assert.Equal("ls", candidates[0].Verb);
        Assert.Equal("/repo", candidates[0].Directory);
        Assert.Equal("git status", candidates[1].Verb);
        Assert.Null(candidates[1].Directory);
    }

    [Fact]
    public void Matches_when_candidate_path_under_entry_directory()
    {
        // Folder-scoped trust compounds: an entry on /home/petabridge
        // covers any candidate whose path is under it.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "find",
            candidateDirectory: "/home/petabridge/.netclaw",
            cwd: null,
            approvedEntries: [new ApprovalEntry("find") { Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Matches_when_candidate_path_equals_entry_directory()
    {
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "find",
            candidateDirectory: "/home/petabridge",
            cwd: null,
            approvedEntries: [new ApprovalEntry("find") { Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Rejects_when_candidate_path_outside_entry_directory()
    {
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "find",
            candidateDirectory: "/home/other",
            cwd: null,
            approvedEntries: [new ApprovalEntry("find") { Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Falls_back_to_cwd_when_candidate_path_is_null()
    {
        // No path argument on the candidate — cwd is the effective directory.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "git status",
            candidateDirectory: null,
            cwd: "/home/petabridge/.netclaw",
            approvedEntries: [new ApprovalEntry("git status") { Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Null_directory_entry_matches_any_candidate()
    {
        // Global wildcard ignores both candidate path and cwd.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "freshdesk",
            candidateDirectory: null,
            cwd: null,
            approvedEntries: [new ApprovalEntry("freshdesk") { Directory = null }]));
    }

    [Fact]
    public void Null_directory_entry_matches_subagent_with_null_cwd_for_netclaw_stats()
    {
        // Regression: a sub-agent that inherits no cwd (parent had none either)
        // invokes `netclaw stats`. The persisted global grant in
        // tool-approvals.json must still auto-approve. Bound to a real verb
        // from the original bug report so a future refactor that re-orders the
        // matcher loop trips this test specifically.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "netclaw stats",
            candidateDirectory: null,
            cwd: null,
            approvedEntries: [new ApprovalEntry("netclaw stats") { Directory = null }]));
    }

    [Fact]
    public void Null_directory_entry_wins_over_folder_scoped_entry_with_null_cwd()
    {
        // Spec scenario "Global grant precedence over folder-scoped grants":
        // when both grants exist for the same verb and the candidate has no
        // cwd, the global grant must still win even though the folder-scoped
        // grant gets skipped.
        ApprovalEntry[] entries =
        [
            new ApprovalEntry("dotnet") { Directory = "/home/user/repos/foo/" },
            new ApprovalEntry("dotnet") { Directory = null },
        ];

        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "dotnet",
            candidateDirectory: null,
            cwd: null,
            approvedEntries: entries));
    }

    [Fact]
    public void IsPureSideEffect_skips_echo_without_redirect()
    {
        Assert.True(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("echo", Directory: null)));
    }

    [Fact]
    public void IsPureSideEffect_does_not_skip_echo_with_redirect_target()
    {
        // echo X > /tmp/log gets /tmp as its directory via the path-arg
        // scan, which means it's no longer "pure" side effect.
        Assert.False(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("echo", Directory: "/tmp")));
    }

    [Fact]
    public void IsPureSideEffect_does_not_skip_action_verbs()
    {
        Assert.False(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("find", Directory: null)));
        Assert.False(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("git push", Directory: null)));
    }

    [Fact]
    public void ExtractCandidates_caps_echo_at_one_token()
    {
        // Without the SingleTokenSideEffectVerbs cap, the verb-chain
        // extractor would capture `echo hello` as a 2-token verb (since
        // `hello` neither starts with `-` nor matches LooksLikeArgument)
        // and the side-effect skip list would not match. Aaron's real
        // dogfood case used `echo "---REMOTE-INFO---"` which already
        // breaks at the leading `-` — but operators routinely run
        // `echo hello`-shape commands in build scripts.
        // (`echo done` would be the more obvious example but `done` is
        // a bash control-flow keyword and triggers IsMessyCompoundCommand,
        // which returns zero candidates.)
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?> { ["Command"] = "echo hello" });

        var c = Assert.Single(candidates);
        Assert.Equal("echo", c.Verb);
        Assert.True(ApprovalPatternMatching.IsPureSideEffect(c));
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_extracts_cd_target_as_directory()
    {
        // Production case: `cd /repo && git remote -v`. The header /
        // persistence layer needs the cd target as the candidate's
        // directory so the prompt shows the meaningful trust scope rather
        // than the per-session ephemeral session_dir, AND so ApprovedSession
        // / ApprovedAlways grants for the verb actually persist (the
        // persistence guard at LlmSessionActor.PersistApprovalCandidatesAsync
        // drops candidates whose effective directory resolves to session_dir
        // — when git remote inherited session_dir as its fallback cwd, that
        // guard silently dropped the grant and the retry threw
        // ToolApprovalRequiredException).
        //
        // ShellSyntaxTree 0.1.4-alpha attributes the cd target as a
        // synthetic IsCwdAttribution arg on every clause that follows
        // a `cd` in the compound; ExtractCandidates surfaces that into
        // the candidate's Directory so the verb's effective directory
        // matches the actual filesystem location the shell will run it in.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /home/user/repos/example && git remote -v"
            });

        Assert.Contains(candidates,
            c => c.Verb == "cd"
              && c.Directory == "/home/user/repos/example");
        Assert.Contains(candidates,
            c => c.Verb == "git remote"
              && c.Directory == "/home/user/repos/example");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_propagates_cd_target_to_subsequent_clauses_with_no_path_arg()
    {
        // Production repro of the retry-after-approval failure on
        // `cd ~/repos && git checkout -b feature/foo`. The git checkout
        // clause has no anchored path arg of its own (feature/foo has a
        // slash but isn't an anchored path token), so before the
        // BashParser rewrite its candidate ended up
        // (git checkout, null) → effective directory fell back to
        // session_dir at persistence time → the session-scratch guard
        // dropped the grant → retry threw ToolApprovalRequiredException.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /home/user/repos/foo && git checkout -b feature/freshdesk-cli-skill"
            });

        Assert.Contains(candidates,
            c => c.Verb == "cd" && c.Directory == "/home/user/repos/foo");
        Assert.Contains(candidates,
            c => c.Verb == "git checkout" && c.Directory == "/home/user/repos/foo");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_tracks_latest_cd_through_multiple_hops()
    {
        // cd /a && cd /b && grep ... — grep inherits /b (the latest cd),
        // not /a. Mirrors the BashParser's state-machine semantics for
        // cd-in-compound; pinning here so a parser regression that
        // forgets cwd updates breaks Netclaw CI before it surfaces in
        // production.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /a && cd /b && pwd"
            });

        Assert.Contains(candidates, c => c.Verb == "cd" && c.Directory == "/a");
        Assert.Contains(candidates, c => c.Verb == "cd" && c.Directory == "/b");
        Assert.Contains(candidates, c => c.Verb == "pwd" && c.Directory == "/b");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_recurses_into_bash_dash_c_with_cd_attribution_intact()
    {
        // bash -c "cd /repo && git push" — the parser flattens the
        // inner command and propagates the cd target onto git push.
        // Recursion is the parser's responsibility; the matcher just
        // reads what it produces.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "bash -c \"cd /repo && git push\""
            });

        Assert.Contains(candidates, c => c.Verb == "git push" && c.Directory == "/repo");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_prefers_explicit_path_arg_over_cd_attribution()
    {
        // When a clause has its own anchored path argument, that wins —
        // the candidate is approved for the specific path it operates
        // on, not for the broader cd target. Approving
        // `dotnet test /home/foo` shouldn't accidentally grant dotnet
        // test access to all of /tmp just because the operator happened
        // to be cd'd there.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /tmp && dotnet test /home/foo"
            });

        Assert.Contains(candidates,
            c => c.Verb == "dotnet test" && c.Directory == "/home/foo");
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_side_effect_verbs_do_not_inherit_cd_attribution()
    {
        // echo / printf / true / false write to stdout and ignore cwd,
        // so cd attribution must NOT attach to them — both because the
        // attribution is semantically meaningless for these verbs and
        // because ApprovalPatternMatching.IsPureSideEffect treats them
        // as unconditional pass when Directory is null (the redirect
        // detector still kicks in if a literal `> /tmp/log` path arg
        // is present on the clause).
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /tmp && echo \"done\""
            });

        Assert.Contains(candidates, c => c.Verb == "echo" && c.Directory == null);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_normalizes_tilde_cd_to_absolute_path_so_clauses_share_one_directory()
    {
        // Production header bug: prompt for `cd ~/x && git checkout -b f`
        // displayed "Saved for this chat: cd, git checkout in 2
        // directories" — cd's candidate kept the raw `~/...` form while
        // git checkout's inherited directory was already absolute (the
        // parser's IsCwdAttribution.Resolved is always pre-expanded).
        // Both should canonicalize to the same absolute path so the
        // distinct-directory counter in the prompt header reads 1.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, "repos", "example");

        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd ~/repos/example && git checkout -b feature/foo"
            });

        Assert.Single(
            candidates
                .Where(c => !string.IsNullOrEmpty(c.Directory))
                .Select(c => c.Directory)
                .Distinct(StringComparer.Ordinal));

        Assert.Contains(candidates, c => c.Verb == "cd" && c.Directory == expected);
        Assert.Contains(candidates, c => c.Verb == "git checkout" && c.Directory == expected);
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void ExtractCandidates_collapses_pipe_chain_into_single_candidate()
    {
        // Pipes stay inside one approval unit — approving cat /etc/hosts
        // | wc -l shouldn't prompt twice. Compare with && which DOES
        // produce independent units.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cat /etc/hosts | wc -l"
            });

        Assert.Single(candidates);
        Assert.Equal("cat", candidates[0].Verb);
        Assert.Equal("/etc/hosts", candidates[0].Directory);  // no extension → no file-parent
    }

    [Fact]
    public void IsApproved_treats_side_effect_candidates_as_authorized()
    {
        // Regression: when a compound command contains both action verbs and
        // pure side-effect clauses (echo "==="), persistence skips the echo
        // but the matcher historically did not. Result: after the user
        // clicked Always anywhere, the action verbs were stored but echo
        // wasn't, so the retry's authorization check saw echo as unapproved
        // and threw ToolApprovalRequiredException — which escaped the
        // already-active approval-pause catch and failed the turn.
        // This test asserts IsApproved skips side-effect candidates the
        // same way persistence does.
        var approvedEntries = new[]
        {
            new ApprovalEntry("cd") { Directory = null },
            new ApprovalEntry("git status") { Directory = null },
            new ApprovalEntry("git remote") { Directory = null }
            // No echo entry — exactly what the side-effect skip produces.
        };

        var compound =
            "cd ~/repo && git status && echo \"---\" && git remote -v && echo \"finished\"";
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            new Dictionary<string, object?> { ["Command"] = compound },
            approvedEntries,
            cwd: null));
    }
}

public sealed class DefaultApprovalMatcherTests
{
    private readonly DefaultApprovalMatcher _matcher = DefaultApprovalMatcher.Instance;

    private static ApprovalEntry Verb(string verb) => new(verb) { Directory = null };

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
        Assert.True(_matcher.IsApproved(
            new ToolName("mcp:memorizer:store"),
            null,
            [Verb("mcp:memorizer:store")],
            cwd: null));
    }

    [Fact]
    public void IsApproved_no_match()
    {
        Assert.False(_matcher.IsApproved(
            new ToolName("mcp:memorizer:store"),
            null,
            [Verb("mcp:memorizer:get")],
            cwd: null));
    }
}
