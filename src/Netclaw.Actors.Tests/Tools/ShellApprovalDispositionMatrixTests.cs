// -----------------------------------------------------------------------
// <copyright file="ShellApprovalDispositionMatrixTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
