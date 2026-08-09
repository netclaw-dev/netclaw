// -----------------------------------------------------------------------
// <copyright file="ShellApprovalDispositionMatrixTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ShellApprovalDispositionMatrixTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "This theory defines Bash authorization behavior. The Windows shell parser does not implement this contract.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The first matrix defines Bash authorization behavior.")]
    [MemberData(nameof(ShellApprovalCases.Rows), MemberType = typeof(ShellApprovalCases))]
    public async Task Shell_approval_contract(string caseId)
    {
        var testCase = ShellApprovalCases.Get(caseId);
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);

        var observed = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(testCase.Expected.Outcome, observed.Outcome);
        Assert.Equal(testCase.Expected.AllowReason, observed.AllowReason);
        Assert.Equal(testCase.Expected.DenyReason, observed.DenyReason);
        Assert.Equal(testCase.Expected.Candidates, observed.CandidateVerbs);
        Assert.Equal(testCase.Expected.IsMessy, observed.IsMessy);
        Assert.Equal(testCase.Expected.ApprovalChecks, harness.ApprovalService.CheckCount);
        Assert.Equal(testCase.Expected.ApprovalMatches, observed.ApprovalMatches);
    }

    [SlopwatchSuppress("SW001", "This regression requires POSIX symlink and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The symlink retry regression defines Bash authorization behavior.")]
    public async Task One_time_retry_rechecks_candidates_that_become_unsafe()
    {
        var testCase = new ShellApprovalCase(
            "one-time-retry-rechecks-safe-candidates",
            new ShellApprovalInvocation("cat leak/secret.txt && git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"]));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);

        var initial = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["git push"], initial.ApprovalContext!.CandidateVerbs);
        harness.SeedOneTimeApproval(initial.ApprovalContext);
        harness.ReplaceProjectDirectoryWithExternalSymlink("leak");

        var retry = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, retry.Outcome);
        Assert.Equal(["cat", "git push"], retry.ApprovalContext!.CandidateVerbs);
    }

    [SlopwatchSuppress("SW001", "This regression requires POSIX glob, symlink, and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The glob retry regression defines Bash authorization behavior.")]
    public async Task One_time_retry_does_not_cover_a_clean_command_that_becomes_messy()
    {
        var testCase = new ShellApprovalCase(
            "one-time-retry-rechecks-clean-to-messy",
            new ShellApprovalInvocation("cat artifacts/* && git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"]));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);
        harness.CreateProjectDirectory("artifacts");

        var initial = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["git push"], initial.ApprovalContext!.CandidateVerbs);
        harness.SeedOneTimeApproval(initial.ApprovalContext);
        harness.CreateProjectFileSymlinkToExternalFile("artifacts/leak");

        var retry = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, retry.Outcome);
        Assert.True(retry.ApprovalContext!.IsMessy);
        Assert.Empty(retry.ApprovalContext.CandidateVerbs);
    }

    [SlopwatchSuppress("SW001", "This regression requires POSIX symlink and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The symlink retry regression defines Bash authorization behavior.")]
    public async Task One_time_retry_rechecks_a_candidate_whose_stored_grant_stops_matching()
    {
        var testCase = new ShellApprovalCase(
            "one-time-retry-rechecks-stored-candidates",
            new ShellApprovalInvocation("git -C repo push && gh pr merge 123"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "git push"),
            ExpectedApproval.Require(["gh pr merge"]));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);
        harness.CreateProjectDirectory("repo");

        var initial = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["gh pr merge"], initial.ApprovalContext!.CandidateVerbs);
        harness.SeedOneTimeApproval(initial.ApprovalContext);
        harness.ReplaceProjectDirectoryWithExternalSymlink("repo");

        var retry = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, retry.Outcome);
        Assert.Equal(["git push", "gh pr merge"], retry.ApprovalContext!.CandidateVerbs);
    }

    [Fact]
    public Task Shell_approval_cases_match_review_table()
        => Verifier.Verify(ShellApprovalCases.RenderReviewTable(), extension: "md");
}

/// <summary>
/// Supplies source-level Slopwatch suppressions without a runtime package dependency.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class SlopwatchSuppressAttribute(string ruleId, string reason) : Attribute
{
    public string RuleId { get; } = ruleId;

    public string Reason { get; } = reason;
}
