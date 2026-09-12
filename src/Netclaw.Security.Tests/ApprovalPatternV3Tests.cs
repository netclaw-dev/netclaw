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
    private static readonly ApprovalEntry BashGitPush =
        ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git", "push"]);

    [Theory]
    [InlineData("git push", new[] { "git", "push" })]
    [InlineData("git push origin", new[] { "git", "push", "origin" })]
    public void Token_prefix_matches_complete_candidate_prefix(
        string verb,
        string[] tokens)
    {
        var candidate = new ApprovalCandidate(verb, Directory: null)
        {
            VerbTokens = Array.AsReadOnly(tokens),
            Shell = ApprovalShell.Bash,
        };

        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
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

        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
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
}
