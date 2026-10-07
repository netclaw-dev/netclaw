// -----------------------------------------------------------------------
// <copyright file="LiteralTwinApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Owner decision F1: each literal twin of a Bash command gets the decision of
/// the typed literal. A loop over literal values gets normal choices, and the
/// grant of one answer covers the next run of the same loop.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class LiteralTwinApprovalTests(ShellApprovalMatrixFixture fixture)
{
    private const string OwnerLoop =
        "for n in 8250 8244; do gh api -X PATCH repos/o/r/issues/$n -f milestone=157 >/dev/null && echo \"moved $n\"; done";

    // The harness evaluates each call in this session.
    private static readonly ToolApprovalSessionId InvocationSession = (ToolApprovalSessionId)"signalr/approval-matrix";

    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [SlopwatchSuppress("SW001", "The Bash 5.2 cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash 5.2 cases require a POSIX host.")]
    public async Task Loop_twins_offer_reusable_choices_and_a_chat_answer_covers_the_loop()
    {
        await using var harness = await CreateHarnessAsync();

        var first = await harness.EvaluateShellAsync(OwnerLoop, Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, first.Outcome);
        var prompt = Assert.IsType<ApprovalPromptObservation>(first.Prompt);
        Assert.False(prompt.IsMessy);
        Assert.Equal(["gh api"], prompt.CandidateVerbs);
        Assert.Contains(ObservedOptionKeys.ApproveSession, prompt.OptionKeys);
        Assert.Contains(ObservedOptionKeys.ApproveAlways, prompt.OptionKeys);
        Assert.Contains(ObservedOptionKeys.ApproveEverywhere, prompt.OptionKeys);

        await AnswerThisChatAsync(harness, OwnerLoop);

        var second = await harness.EvaluateShellDecisionAsync(OwnerLoop, Ct);
        Assert.Equal(ToolAuthorizationOutcome.Allowed, second.Outcome);
        Assert.Equal(ToolAllowReason.StoredApproval, second.AllowReason);

        // Negative control: the "gh api" grant does not cover other command words.
        var other = await harness.EvaluateShellDecisionAsync(
            "for n in 8250 8244; do gh pr merge $n; done",
            Ct);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, other.Outcome);
    }

    // SECURITY: the twin of a value outside the folder keeps its path scope, so
    // a folder grant from the first answer does not cover it.
    [SlopwatchSuppress("SW001", "The Bash 5.2 cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash 5.2 cases require a POSIX host.")]
    public async Task Folder_answer_does_not_cover_a_twin_outside_the_folder()
    {
        await using var harness = await CreateHarnessAsync();
        const string inside = "for d in a.slnx b.slnx; do dotnet build \"$d\"; done";

        await AnswerAsync(harness, inside, GrantScopeKind.Folder);

        var covered = await harness.EvaluateShellDecisionAsync(inside, Ct);
        var outside = await harness.EvaluateShellDecisionAsync(
            "for d in a.slnx ../outside/x.slnx; do dotnet build \"$d\"; done",
            Ct);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, covered.Outcome);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, outside.Outcome);
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync()
        => ShellApprovalHarness.CreateAsync(
            "literal-twins",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
            Approvals.None,
            fixture.ActorSystem,
            Ct);

    private static Task AnswerThisChatAsync(ShellApprovalHarness harness, string command)
        => AnswerAsync(harness, command, GrantScopeKind.Session);

    // The session actor builds and records the grants of the answer.
    private static async Task AnswerAsync(
        ShellApprovalHarness harness,
        string command,
        GrantScopeKind scope)
    {
        var prompt = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, prompt.Outcome);
        var approval = Assert.IsType<ToolApprovalContext>(prompt.ApprovalContext);
        var grants = GrantBuilder.Build(
            approval.Candidates!,
            scope,
            approval.Cwd,
            harness.SessionDirectory,
            approval.RepositoryCommonDirectory);
        await harness.ApprovalService.RecordApprovalCandidatesAsync(
            InvocationSession,
            TrustAudience.Personal,
            new ToolName(ShellTool.ToolName),
            grants,
            Ct);
    }
}
