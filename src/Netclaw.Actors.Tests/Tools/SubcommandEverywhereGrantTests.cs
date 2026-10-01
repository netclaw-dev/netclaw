// -----------------------------------------------------------------------
// <copyright file="SubcommandEverywhereGrantTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// An "Always anywhere" answer for one <c>gh</c> or <c>git</c> subcommand must save
/// that subcommand only. A bare <c>gh</c> or <c>git</c> grant covers every
/// subcommand, which includes <c>gh auth logout</c> and <c>git filter-branch</c>.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class SubcommandEverywhereGrantTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] GhUnrelated = ["gh auth logout"];

    private static readonly string[] GitUnrelated =
    [
        "git push --force origin main",
        "git filter-branch --force HEAD",
    ];

    public static TheoryData<string> Commands =>
    [
        "gh pr view 123",
        "gh api repos/o/r/contents/x",
        "gh -R o/r pr view 123",
        "gh --repo o/r pr list",
        "gh auth status",
        "git fetch origin",
        "git --no-pager log -1",
        "git -C /some/repo status",
    ];

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [MemberData(nameof(Commands))]
    public async Task Everywhere_grant_covers_the_subcommand_and_not_the_bare_executable(string command)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "everywhere-subcommand-grant",
            new ShellApprovalInvocation("true"),
            Approvals.None,
            fixture.ActorSystem,
            Ct);
        var executable = command.Split(' ')[0];

        var prompt = await harness.EvaluateShellDecisionAsync(command, Ct);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, prompt.Outcome);
        var approval = Assert.IsType<ToolApprovalContext>(prompt.ApprovalContext);
        Assert.Contains(approval.Options, option => option.Key.Value == ApprovalOptionKeys.ApproveEverywhere);

        // The session actor builds the grants of an "Always anywhere" answer here.
        var grants = GrantBuilder.Build(
            approval.Candidates!,
            GrantScopeKind.Everywhere,
            approval.Cwd,
            harness.SessionDirectory,
            approval.RepositoryCommonDirectory);
        await harness.ApprovalService.RecordApprovalCandidatesAsync(
            (ToolApprovalSessionId)"signalr/other-session",
            TrustAudience.Personal,
            new ToolName(ShellTool.ToolName),
            grants,
            Ct);

        var stored = harness.GetStoredShellEntries(TrustAudience.Personal);
        Assert.NotEmpty(stored);
        Assert.All(stored, entry =>
        {
            Assert.Null(entry.Directory);
            Assert.NotEqual([executable], entry.VerbTokens!);
        });

        // Positive control: the saved grant covers the approved call.
        var reused = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.Equal(ToolAuthorizationOutcome.Allowed, reused.Outcome);
        Assert.Equal(ToolAllowReason.StoredApproval, reused.AllowReason);

        // Negative control: the saved grant does not cover an unrelated subcommand.
        foreach (var unrelated in executable == "gh" ? GhUnrelated : GitUnrelated)
        {
            var other = await harness.EvaluateShellDecisionAsync(unrelated, Ct);
            Assert.True(
                other.Outcome == ToolAuthorizationOutcome.RequiresApproval,
                $"'{unrelated}' was {other.Outcome} after an everywhere grant for '{command}'. "
                + $"Stored: {string.Join(", ", stored.Select(entry => $"[{string.Join(' ', entry.VerbTokens!)}]"))}");
        }
    }
}
