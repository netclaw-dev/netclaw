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
/// A saved shell grant covers exactly its verb chain, with any arguments. It
/// never covers a longer chain: a <c>gh</c> grant covers <c>gh --help</c>, not
/// <c>gh auth logout</c>. When an option comes before the subcommand, the
/// parser cannot prove the verb chain, so the prompt offers one-time consent only.
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

    private static readonly ToolApprovalSessionId OtherSession = (ToolApprovalSessionId)"signalr/other-session";

    public static TheoryData<string, string[]> SubcommandFirstCommands => new()
    {
        { "gh pr view 123", ["gh", "pr", "view"] },
        { "gh pr view 123 -R o/r", ["gh", "pr", "view"] },
        { "gh api repos/o/r/contents/x", ["gh", "api"] },
        { "gh auth status", ["gh", "auth", "status"] },
        { "git fetch origin", ["git", "fetch", "origin"] },
        { "git -C /some/repo status", ["git", "status"] },
    };

    public static TheoryData<string> OptionFirstCommands =>
    [
        "gh -R o/r pr view 123",
        "gh --repo o/r pr list",
        "git --no-pager log -1",
    ];

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [MemberData(nameof(SubcommandFirstCommands))]
    public async Task Everywhere_grant_saves_the_exact_verb_chain(string command, string[] expectedChain)
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        var executable = command.Split(' ')[0];

        var stored = await ApproveEverywhereAsync(harness, command);

        var entry = Assert.Single(stored);
        Assert.Null(entry.Directory);
        Assert.Equal(expectedChain, entry.VerbTokens!);

        // Positive control: the saved grant covers the approved call.
        await AssertAllowedByStoredGrantAsync(harness, command);

        // Negative control: the saved grant does not cover an unrelated subcommand.
        foreach (var unrelated in executable == "gh" ? GhUnrelated : GitUnrelated)
            await AssertNeedsApprovalAsync(harness, unrelated, stored);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [MemberData(nameof(OptionFirstCommands))]
    public async Task Option_before_the_subcommand_offers_once_only(string command)
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        var executable = command.Split(' ')[0];

        var prompt = await harness.EvaluateShellDecisionAsync(command, Ct);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, prompt.Outcome);
        var approval = Assert.IsType<ToolApprovalContext>(prompt.ApprovalContext);
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            approval.Options.Select(option => option.Key.Value));

        // A forged "Always anywhere" answer cannot save a grant: the candidate has no verb chain.
        var grants = GrantBuilder.Build(
            approval.Candidates!,
            GrantScopeKind.Everywhere,
            approval.Cwd,
            harness.SessionDirectory,
            approval.RepositoryCommonDirectory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(harness, grants));
        Assert.Empty(harness.GetStoredShellEntries(TrustAudience.Personal));

        foreach (var unrelated in executable == "gh" ? GhUnrelated : GitUnrelated)
            await AssertNeedsApprovalAsync(harness, unrelated, []);
    }

    // The owner's case: approving "gh --help" must not trust every gh subcommand.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Bare_program_grant_covers_options_only()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);

        var stored = await ApproveEverywhereAsync(harness, "gh --help");

        Assert.Equal(["gh"], Assert.Single(stored).VerbTokens!);
        await AssertNeedsApprovalAsync(harness, "gh repo clone x", stored);
        await AssertNeedsApprovalAsync(harness, "gh auth logout", stored);
        await AssertNeedsApprovalAsync(harness, "gh -R o/r auth logout", stored);
        await AssertAllowedByStoredGrantAsync(harness, "gh --version");
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Subcommand_grant_does_not_cover_a_sibling_subcommand()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);

        var stored = await ApproveEverywhereAsync(harness, "gh pr view 1");

        Assert.Equal(["gh", "pr", "view"], Assert.Single(stored).VerbTokens!);
        await AssertNeedsApprovalAsync(harness, "gh pr merge 1", stored);
        await AssertAllowedByStoredGrantAsync(harness, "gh pr view 2 --web");
    }

    // A legacy phrase also must equal the whole parser verb chain. The parser
    // keeps v1.5.1 in the chain, but it ends the chain at 1.5.1 and --force.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Legacy_exact_grant_does_not_cover_a_longer_verb_chain()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        harness.AddStoredShellEntry(
            TrustAudience.Personal,
            ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "git push origin"));
        var stored = harness.GetStoredShellEntries(TrustAudience.Personal);

        await AssertNeedsApprovalAsync(harness, "git push origin v1.5.1 --force", stored);
        await AssertNeedsApprovalAsync(harness, "git push origin main --force", stored);
        await AssertAllowedByStoredGrantAsync(harness, "git push origin 1.5.1 --force");
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Single_token_program_grant_covers_its_options()
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("ls"));

        await AssertAllowedByStoredGrantAsync(harness, "ls -la");
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(ApprovalState approvals)
        => ShellApprovalHarness.CreateAsync(
            "everywhere-subcommand-grant",
            new ShellApprovalInvocation("true"),
            approvals,
            fixture.ActorSystem,
            Ct);

    // The session actor builds and records the grants of an "Always anywhere" answer.
    private static async Task<IReadOnlyList<ApprovalEntry>> ApproveEverywhereAsync(
        ShellApprovalHarness harness,
        string command)
    {
        var prompt = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, prompt.Outcome);
        var approval = Assert.IsType<ToolApprovalContext>(prompt.ApprovalContext);
        Assert.Contains(approval.Options, option => option.Key.Value == ApprovalOptionKeys.ApproveEverywhere);

        var grants = GrantBuilder.Build(
            approval.Candidates!,
            GrantScopeKind.Everywhere,
            approval.Cwd,
            harness.SessionDirectory,
            approval.RepositoryCommonDirectory);
        await RecordAsync(harness, grants);
        return harness.GetStoredShellEntries(TrustAudience.Personal);
    }

    private static Task RecordAsync(ShellApprovalHarness harness, IReadOnlyList<ToolApprovalGrant> grants)
        => harness.ApprovalService.RecordApprovalCandidatesAsync(
            OtherSession,
            TrustAudience.Personal,
            new ToolName(ShellTool.ToolName),
            grants,
            Ct);

    private static async Task AssertAllowedByStoredGrantAsync(ShellApprovalHarness harness, string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.Allowed
            && decision.AllowReason == ToolAllowReason.StoredApproval,
            $"'{command}' was {decision.Outcome} ({decision.AllowReason}); a stored grant should cover it.");
    }

    private static async Task AssertNeedsApprovalAsync(
        ShellApprovalHarness harness,
        string command,
        IReadOnlyList<ApprovalEntry> stored)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.RequiresApproval,
            $"'{command}' was {decision.Outcome}. "
            + $"Stored: {string.Join(", ", stored.Select(entry => $"[{entry.Verb}]"))}");
    }
}
