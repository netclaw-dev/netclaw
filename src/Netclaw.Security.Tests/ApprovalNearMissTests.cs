// -----------------------------------------------------------------------
// <copyright file="ApprovalNearMissTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Tests for the near misses that <see cref="ApprovalPatternMatching.EvaluateShellApproval"/>
/// returns when a persisted grant for the same executable does not cover a candidate.
/// </summary>
public sealed class ApprovalNearMissTests
{
    [Fact]
    public void Typed_evaluation_returns_token_mismatch_from_one_grant_pass()
    {
        var candidate = new ApprovalCandidate("git push", Directory: null)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = ["git", "push"],
        };
        var grant = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            ["git", "status"]);

        var evaluation = ApprovalPatternMatching.EvaluateShellApproval(
            candidate,
            cwd: "/work",
            [grant],
            maximumNearMisses: 1);

        Assert.Null(evaluation.MatchedEntry);
        var nearMiss = Assert.Single(evaluation.NearMisses);
        Assert.Same(grant, nearMiss.Grant);
        Assert.Equal(ShellApprovalNearMissReason.TokenMismatch, nearMiss.Reason);
    }

    [Fact]
    public void Typed_evaluation_returns_shell_mismatch_from_one_grant_pass()
    {
        var candidate = new ApprovalCandidate("git status", Directory: null)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = ["git", "status"],
        };
        var grant = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.PowerShell,
            ["git", "status"]);

        var evaluation = ApprovalPatternMatching.EvaluateShellApproval(
            candidate,
            cwd: "/work",
            [grant],
            maximumNearMisses: 1);

        Assert.Null(evaluation.MatchedEntry);
        var nearMiss = Assert.Single(evaluation.NearMisses);
        Assert.Same(grant, nearMiss.Grant);
        Assert.Equal(ShellApprovalNearMissReason.ShellMismatch, nearMiss.Reason);
    }

    [Fact]
    public void Typed_evaluation_enumerates_the_grant_snapshot_once()
    {
        var candidate = new ApprovalCandidate("git push", Directory: null)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = ["git", "push"],
        };
        var grant = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            ["git", "push"],
            "/work/repository");
        var enumerationCount = 0;

        IEnumerable<ApprovalEntry> Snapshot()
        {
            enumerationCount++;
            if (enumerationCount > 1)
                throw new InvalidOperationException("The grant snapshot was enumerated more than once.");

            yield return grant;
        }

        var evaluation = ApprovalPatternMatching.EvaluateShellApproval(
            candidate,
            cwd: "/work/other",
            Snapshot(),
            maximumNearMisses: 1);

        Assert.Null(evaluation.MatchedEntry);
        Assert.Equal(ShellApprovalNearMissReason.OutsideDirectory, Assert.Single(evaluation.NearMisses).Reason);
        Assert.Equal(1, enumerationCount);
    }
}
