// -----------------------------------------------------------------------
// <copyright file="ShellWrapperAssignmentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Boundary tests for an assignment prefix on a bundled shell wrapper. Each
/// input goes through the production tool executor for a Personal session
/// that holds a stored <c>git push</c> grant.
/// </summary>
/// <remarks>
/// The wrapper's assignment reaches the child environment. For example,
/// <c>GIT_SSH_COMMAND=... bash -lc "git push"</c> changes what the child
/// <c>git push</c> runs. A grant for a plain <c>git push</c> must not cover
/// that call.
/// </remarks>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ShellWrapperAssignmentTests(ShellApprovalMatrixFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApprovalState GitPushGrant => Approvals.PersistentAnywhere("git push");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plain_git_push_matches_the_stored_grant(bool bash52)
    {
        var observed = await EvaluateAsync(
            "git push",
            Host(bash52),
            ToolApprovalMode.Approval,
            interactive: true);

        Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, observed.AllowReason);
    }

    [Theory]
    [InlineData("GIT_SSH_COMMAND=evil bash -lc \"git push\"", false)]
    [InlineData("GIT_SSH_COMMAND=evil bash -lc \"git push\"", true)]
    [InlineData("GIT_SSH_COMMAND=evil sh -lc \"git push\"", false)]
    [InlineData("GIT_SSH_COMMAND=evil sh -lc \"git push\"", true)]
    [InlineData("GIT_SSH_COMMAND=evil sh -c \"git push\"", false)]
    [InlineData("GIT_SSH_COMMAND=evil sh -c \"git push\"", true)]
    [InlineData("X=1 GIT_SSH_COMMAND=evil bash -lc \"git push\"", false)]
    [InlineData("X=1 GIT_SSH_COMMAND=evil bash -lc \"git push\"", true)]
    [InlineData("X=1 GIT_SSH_COMMAND=evil sh -lc \"git push\"", false)]
    [InlineData("X=1 GIT_SSH_COMMAND=evil sh -lc \"git push\"", true)]
    [InlineData("X=1 GIT_SSH_COMMAND=evil sh -c \"git push\"", false)]
    [InlineData("X=1 GIT_SSH_COMMAND=evil sh -c \"git push\"", true)]
    public async Task Wrapper_assignment_prefix_does_not_match_a_plain_child_grant(
        string command,
        bool bash52)
    {
        var approvalInteractive = await EvaluateAsync(
            command, Host(bash52), ToolApprovalMode.Approval, interactive: true);
        var approvalUnattended = await EvaluateAsync(
            command, Host(bash52), ToolApprovalMode.Approval, interactive: false);
        var autoInteractive = await EvaluateAsync(
            command, Host(bash52), ToolApprovalMode.Auto, interactive: true);
        var autoUnattended = await EvaluateAsync(
            command, Host(bash52), ToolApprovalMode.Auto, interactive: false);

        // Approval mode, interactive: only an exact one-time prompt.
        Assert.Equal(ApprovalOutcome.RequiresApproval, approvalInteractive.Outcome);
        Assert.Empty(approvalInteractive.ApprovalMatches);
        Assert.True(approvalInteractive.Prompt!.IsMessy);
        Assert.Equal(["approve_once", "deny"], approvalInteractive.Prompt.OptionKeys);

        // Auto mode, interactive: the Auto policy decides, as it does for a
        // plain git push. The stored grant is not a reason for the result.
        Assert.Equal(ApprovalOutcome.Allowed, autoInteractive.Outcome);
        Assert.Equal(ApprovalAllowReason.PolicyAuto, autoInteractive.AllowReason);
        Assert.Empty(autoInteractive.ApprovalMatches);

        // Unattended: no operator can give exact consent in either mode.
        foreach (var unattended in new[] { approvalUnattended, autoUnattended })
        {
            Assert.Equal(ApprovalOutcome.Denied, unattended.Outcome);
            Assert.Equal("shell_unresolved_trust_zone_input", unattended.DenyReason);
        }
    }

    // The unresolved result must not hide a hard denial in the child.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrapper_assignment_prefix_keeps_a_child_hard_denial(bool bash52)
    {
        foreach (var mode in new[] { ToolApprovalMode.Approval, ToolApprovalMode.Auto })
        {
            foreach (var interactive in new[] { true, false })
            {
                var observed = await EvaluateAsync(
                    "GIT_SSH_COMMAND=evil bash -lc \"netclaw daemon stop\"",
                    Host(bash52),
                    mode,
                    interactive);

                Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
                Assert.Equal("hard_deny_self_destructive", observed.DenyReason);
            }
        }
    }

    private static ShellApprovalHost Host(bool bash52)
        => bash52 ? ShellApprovalHost.Bash52 : ShellApprovalHost.Bash;

    private async Task<ApprovalObservation> EvaluateAsync(
        string command,
        ShellApprovalHost host,
        ToolApprovalMode mode,
        bool interactive)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            $"wrapper-assignment-{(interactive ? "interactive" : "unattended")}",
            new ShellApprovalInvocation(command, Interactive: interactive, Host: host),
            GitPushGrant,
            fixture.ActorSystem,
            Ct,
            shellApprovalMode: mode);
        var observed = await harness.EvaluateAsync(Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{command} | {host} | {mode} | interactive={interactive} | {observed.Outcome} | "
            + $"allow={observed.AllowReason} | deny={observed.DenyReason} | "
            + $"matches={string.Join(",", observed.ApprovalMatches)} | "
            + $"candidates={string.Join(",", observed.Prompt?.CandidateVerbs ?? [])} | "
            + $"messy={observed.Prompt?.IsMessy} | "
            + $"options={string.Join(",", observed.Prompt?.OptionKeys ?? [])}");
        return observed;
    }
}
