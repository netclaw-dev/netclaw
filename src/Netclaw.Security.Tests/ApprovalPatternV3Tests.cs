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

    // A verb grant covers its words and any later words: the later words are
    // arguments (owner decision, 2026-10-05).
    [Theory]
    [InlineData("git push", new[] { "git", "push" }, true)]
    [InlineData("git push origin", new[] { "git", "push", "origin" }, true)]
    [InlineData("git push origin main", new[] { "git", "push", "origin", "main" }, true)]
    [InlineData("git pull origin", new[] { "git", "pull", "origin" }, false)]
    public void Token_prefix_covers_its_words_and_later_words(
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

        // The parser chain "git ls-tree feature" starts with the grant words.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: null,
            [grant]));
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

    // A legacy phrase gets the rule of a new grant for its words: it covers
    // its words and any later words, but no other word in its own positions.
    [Theory]
    [InlineData(new[] { "git", "push", "origin" }, true)]
    [InlineData(new[] { "git", "push", "origin", "v1.5.1" }, true)]
    [InlineData(new[] { "git", "push", "upstream" }, false)]
    [InlineData(new[] { "git", "push" }, false)]
    public void Legacy_exact_covers_its_words_and_later_words(string[] tokens, bool expected)
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

    // A legacy phrase is its command words. The display verb does not count:
    // "dotnet list package --vulnerable" shows "dotnet list".
    [Theory]
    [InlineData("dotnet list", new[] { "dotnet", "list", "package" }, true)]
    [InlineData("dotnet list package --vulnerable", new[] { "dotnet", "list", "package" }, true)]
    [InlineData("dotnet list", new[] { "dotnet", "list", "reference" }, false)]
    [InlineData("dotnet list", new[] { "dotnet", "list" }, false)]
    public void Legacy_exact_matches_its_own_words_whatever_the_display(
        string display,
        string[] tokens,
        bool expected)
    {
        var grant = ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "dotnet list package");
        var candidate = new ApprovalCandidate(display, Directory: null)
        {
            VerbTokens = Array.AsReadOnly(tokens),
            Shell = ApprovalShell.Bash,
        };

        Assert.Equal(expected, ApprovalPatternMatching.MatchesShellApproval(candidate, cwd: null, [grant]));
    }

    // A legacy program-only phrase stays exact, as a new program-only grant does.
    [Fact]
    public void Legacy_program_phrase_does_not_cover_a_verb()
    {
        var grant = ApprovalEntry.CreateLegacyExact(
            ApprovalShell.Bash,
            "gh");
        var candidate = new ApprovalCandidate("gh auth logout", Directory: null)
        {
            VerbTokens = Array.AsReadOnly(["gh", "auth", "logout"]),
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
