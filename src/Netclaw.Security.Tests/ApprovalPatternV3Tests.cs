// -----------------------------------------------------------------------
// <copyright file="ApprovalPatternV3Tests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ApprovalPatternV3Tests
{
    private static readonly ApprovalAssignmentDigest AssignmentDigest =
        new($"sha256:{new string('a', 64)}");

    private static readonly ApprovalEntry BashGitPush =
        ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git", "push"]);

    // A grant covers exactly its verb chain (#2306): only the equal chain matches.
    [Theory]
    [InlineData("git push", new[] { "git", "push" }, true)]
    [InlineData("git push origin", new[] { "git", "push", "origin" }, false)]
    public void Token_prefix_matches_complete_candidate_prefix(
        string verb,
        string[] tokens,
        bool expected)
    {
        var candidate = new ApprovalCandidate(verb, Directory: null)
        {
            VerbTokens = Array.AsReadOnly(tokens),
            Shell = ApprovalShell.Bash,
        };

        Assert.Equal(expected, ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: null,
            [BashGitPush]));
    }

    [Fact]
    public void Token_prefix_does_not_cross_shell_boundary()
    {
        var candidate = new ApprovalCandidate("git push", Directory: null)
        {
            VerbTokens = Array.AsReadOnly(["git", "push"]),
            Shell = ApprovalShell.PowerShell,
        };

        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: null,
            [BashGitPush]));
    }

    [Fact]
    public void Typed_shell_grant_does_not_match_candidate_without_shell_facts()
    {
        var candidate = new ApprovalCandidate("git push", Directory: null);

        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: null,
            [BashGitPush]));
    }

    [Fact]
    public void PowerShell_token_and_directory_match_ignore_case_on_all_hosts()
    {
        var grant = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.PowerShell,
            ["Get-Content"],
            @"C:\Work\Repo");
        var candidate = new ApprovalCandidate("get-content", @"c:\work\repo\src")
        {
            Shell = ApprovalShell.PowerShell,
            VerbTokens = Array.AsReadOnly(["get-content"]),
        };

        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: null,
            [grant]));
    }

    [Theory]
    [InlineData("git pull", new[] { "git", "pull" })]
    [InlineData("git push", new[] { "git" })]
    [InlineData("git push", new[] { "git", "push value" })]
    public void Token_prefix_rejects_mismatch_or_invalid_tokens(
        string verb,
        string[] tokens)
    {
        var candidate = new ApprovalCandidate(verb, Directory: null)
        {
            VerbTokens = Array.AsReadOnly(tokens),
            Shell = ApprovalShell.Bash,
        };

        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: null,
            [BashGitPush]));
    }

    [Fact]
    public void Token_prefix_uses_parser_tokens_when_legacy_projection_is_shorter()
    {
        var grant = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            ["git", "ls-tree"]);
        var candidate = new ApprovalCandidate("git ls-tree", Directory: null)
        {
            VerbTokens = Array.AsReadOnly(["git", "ls-tree", "feature"]),
            Shell = ApprovalShell.Bash,
        };

        // The parser chain "git ls-tree feature" is longer than the grant (#2306).
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: null,
            [grant]));
    }

    // A grant covers exactly its verb chain. It never covers a longer chain.
    [Theory]
    [InlineData("git push origin", new[] { "git", "push", "origin" })]
    [InlineData("git push origin main", new[] { "git", "push", "origin", "main" })]
    public void Token_grant_does_not_match_a_longer_verb_chain(
        string verb,
        string[] tokens)
    {
        var candidate = new ApprovalCandidate(verb, Directory: null)
        {
            VerbTokens = Array.AsReadOnly(tokens),
            Shell = ApprovalShell.Bash,
        };

        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: null,
            [BashGitPush]));
    }

    // Policy data gives echo and which a one-token chain, so the parser's
    // folded word is an argument. gh has no such policy, so "gh auth" is a chain.
    [Theory]
    [InlineData("echo", new[] { "echo", "hi" }, true)]
    [InlineData("which", new[] { "which", "gh" }, true)]
    [InlineData("gh", new[] { "gh", "auth" }, false)]
    public void Bare_program_grant_covers_folded_operands_only_for_single_token_programs(
        string program,
        string[] tokens,
        bool expected)
    {
        var grant = ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, [program]);
        var candidate = new ApprovalCandidate(program, Directory: null)
        {
            VerbTokens = Array.AsReadOnly(tokens),
            Shell = ApprovalShell.Bash,
        };

        Assert.Equal(expected, ApprovalPatternMatching.MatchesShellApproval(candidate, cwd: null, [grant]));
    }

    // gh -R o/r auth logout: the parser chain stops at "gh", so it is unproven.
    // Only an exact-command grant for the identical words covers it.
    [Fact]
    public void Unproven_verb_chain_matches_only_the_identical_exact_command()
    {
        var words = new[] { "gh", "-R", "o/r", "auth", "logout" };
        var bareGh = ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["gh"]);
        var legacyGh = ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "gh");
        var exact = ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, words);
        var otherExact = ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["gh", "-R", "o/r", "auth", "status"]);
        var candidate = new ApprovalCandidate("gh", Directory: null)
        {
            VerbTokens = Array.AsReadOnly(words),
            Shell = ApprovalShell.Bash,
            HasUnprovenVerbChain = true,
        };

        Assert.False(ApprovalPatternMatching.MatchesShellApproval(candidate, cwd: null, [bareGh]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(candidate, cwd: null, [legacyGh]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(candidate, cwd: null, [otherExact]));
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(candidate, cwd: null, [exact]));

        // An exact-command grant never covers a proved chain of the bare program.
        var bareCall = new ApprovalCandidate("gh", Directory: null)
        {
            VerbTokens = Array.AsReadOnly(["gh"]),
            Shell = ApprovalShell.Bash,
        };
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(bareCall, cwd: null, [exact]));
    }

    // A legacy phrase also needs the whole chain of the candidate tokens.
    [Theory]
    [InlineData(new[] { "git", "push", "origin" }, true)]
    [InlineData(new[] { "git", "push", "origin", "v1.5.1" }, false)]
    public void Legacy_exact_matches_the_whole_parser_verb_chain(string[] tokens, bool expected)
    {
        var grant = ApprovalEntry.CreateLegacyExact(
            ApprovalShell.Bash,
            "git push origin");
        var candidate = new ApprovalCandidate("git push origin", Directory: null)
        {
            VerbTokens = Array.AsReadOnly(tokens),
            Shell = ApprovalShell.Bash,
        };

        Assert.Equal(expected, ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: null,
            [grant]));
    }

    [Fact]
    public void Legacy_exact_does_not_match_a_longer_candidate()
    {
        var grant = ApprovalEntry.CreateLegacyExact(
            ApprovalShell.Bash,
            "git push");
        var candidate = new ApprovalCandidate("git push origin", Directory: null)
        {
            VerbTokens = Array.AsReadOnly(["git", "push", "origin"]),
            Shell = ApprovalShell.Bash,
        };

        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: null,
            [grant]));
    }

    [Fact]
    public void Assignment_qualified_grant_requires_the_same_exact_constraint()
    {
        var grant = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            ["inspect"],
            assignmentDigest: AssignmentDigest);
        var matching = new ApprovalCandidate("inspect", Directory: null)
        {
            AssignmentDigest = AssignmentDigest,
            VerbTokens = ["inspect"],
            Shell = ApprovalShell.Bash,
        };
        var unqualified = matching with
        {
            AssignmentDigest = null,
        };

        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            matching,
            cwd: null,
            [grant]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            unqualified,
            cwd: null,
            [grant]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            matching,
            cwd: null,
            [ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["inspect"])]));
    }

    [Fact]
    public void Assignment_qualified_side_effect_is_not_approval_exempt()
    {
        var candidate = new ApprovalCandidate(
            "echo",
            Directory: null)
        {
            AssignmentDigest = AssignmentDigest,
        };

        Assert.False(ApprovalPatternMatching.IsPureSideEffect(candidate));
    }
}
