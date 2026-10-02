// -----------------------------------------------------------------------
// <copyright file="ExactVerbChainMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// A shell grant covers exactly its verb chain. A mutant that turns the
/// equality check back into prefix matching lets a "gh" grant cover
/// "gh auth logout", so these tests must reject it.
/// </summary>
public sealed class ExactVerbChainMutationTests
{
    [Fact]
    public void Grant_covers_the_equal_verb_chain()
    {
        Assert.True(Matches(Grant("gh"), "gh"));
        Assert.True(Matches(Grant("gh", "pr", "view"), "gh", "pr", "view"));
    }

    [Fact]
    public void Bare_program_grant_does_not_cover_a_subcommand()
    {
        Assert.False(Matches(Grant("gh"), "gh", "auth", "logout"));
        Assert.False(Matches(Grant("git"), "git", "push"));
    }

    [Fact]
    public void Subcommand_grant_does_not_cover_a_longer_or_shorter_chain()
    {
        Assert.False(Matches(Grant("git", "push"), "git", "push", "origin"));
        Assert.False(Matches(Grant("gh", "pr", "view"), "gh", "pr"));
    }

    [Fact]
    public void Legacy_phrase_does_not_cover_a_longer_chain()
    {
        var legacy = ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "git push origin");

        Assert.True(Matches(legacy, "git", "push", "origin"));
        Assert.False(Matches(legacy, "git", "push", "origin", "v1.5.1"));
    }

    [Fact]
    public void A_word_with_a_digit_ends_the_verb_chain()
    {
        Assert.Equal(["git", "show"], ShellApprovalMatcher.CanonicalVerbTokens(["git", "show", "b42bf5a"], null));
        Assert.Equal(["git", "push", "origin"], ShellApprovalMatcher.CanonicalVerbTokens(["git", "push", "origin", "v0.4.0"], null));
        Assert.Equal(["git", "push", "origin"], ShellApprovalMatcher.CanonicalVerbTokens(["git", "push", "origin"], null));
        Assert.Equal(["gh", "pr", "view"], ShellApprovalMatcher.CanonicalVerbTokens(["gh", "pr", "view"], null));
        Assert.Equal(["python3", "tool"], ShellApprovalMatcher.CanonicalVerbTokens(["python3", "tool"], null));
        Assert.Equal(["git"], ShellApprovalMatcher.CanonicalVerbTokens(["git", "2fa"], null));
    }

    private static ApprovalEntry Grant(params string[] tokens)
        => ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, tokens);

    private static bool Matches(ApprovalEntry grant, params string[] tokens)
        => ApprovalPatternMatching.MatchesShellApproval(
            new ApprovalCandidate(string.Join(' ', tokens), Directory: null)
            {
                VerbTokens = Array.AsReadOnly(tokens),
                Shell = ApprovalShell.Bash,
            },
            cwd: null,
            [grant]);
}
