// -----------------------------------------------------------------------
// <copyright file="LegacyShellApprovalNearMissTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class LegacyShellApprovalNearMissTests(ShellApprovalMatrixFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Legacy_exact_assignment_mismatch_trace_keeps_the_same_consent_options()
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "legacy-shell-assignment-near-miss",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
            Approvals.None,
            fixture.ActorSystem,
            Ct);
        const string command = "export DOTNET_ROOT='/usr/share/dotnet'; dotnet test";

        var withoutGrant = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, withoutGrant.Outcome);
        var baselinePrompt = Assert.IsType<ToolApprovalContext>(withoutGrant.ApprovalContext);

        harness.AddStoredShellEntry(
            TrustAudience.Personal,
            ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "dotnet test"));

        var withLegacyGrant = await harness.EvaluateShellDecisionAsync(command, Ct);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, withLegacyGrant.Outcome);
        var nearMissPrompt = Assert.IsType<ToolApprovalContext>(withLegacyGrant.ApprovalContext);
        Assert.Equal(baselinePrompt.CandidateVerbs, nearMissPrompt.CandidateVerbs);
        Assert.Equal(
            baselinePrompt.Options.Select(static option => option.Key.Value),
            nearMissPrompt.Options.Select(static option => option.Key.Value));

        var nearMiss = Assert.Single(
            withLegacyGrant.ShellPolicyTrace.Rows,
            static row => row.Stage == ShellPolicyTraceStage.StoredGrantMatch
                          && row.Outcome == ShellPolicyTraceOutcome.Uncovered
                          && row.ExecutableBasename == "dotnet");
        Assert.Equal(ShellPolicyTraceReason.AssignmentMismatch, nearMiss.Reason);
    }
}
