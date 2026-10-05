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
/// A saved shell grant covers exactly its command words (the ShellSyntaxTree
/// <c>CommandWords</c> fact), with any arguments. Every option order of one
/// command has the same words. A grant never covers other words: a <c>gh</c>
/// grant covers <c>gh --help</c>, not <c>gh auth logout</c>. After the verb
/// slot, option values, expansions, and globs are arguments. Unknown words
/// (only in the verb slot) get a rewrite correction: no run and no prompt.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class SubcommandEverywhereGrantTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] GhUnrelated = ["gh auth logout", "gh repo clone x"];

    private static readonly string[] GitUnrelated =
    [
        "git push --force origin main",
        "git filter-branch --force HEAD",
    ];

    private static readonly ToolApprovalSessionId OtherSession = (ToolApprovalSessionId)"signalr/other-session";

    public static TheoryData<string, string[]> Commands => new()
    {
        // gh pr view is a reviewed diagnostic and needs no grant, so the bare
        // forms use gh pr comment, which still prompts.
        { "gh pr comment 123", ["gh", "pr", "comment"] },
        { "gh pr comment 123 -R o/r", ["gh", "pr", "comment"] },
        { "gh -R o/r pr view 123", ["gh", "pr", "view"] },
        { "gh --repo o/r pr list", ["gh", "pr", "list"] },
        { "gh api repos/o/r/contents/x", ["gh", "api"] },
        { "gh auth status", ["gh", "auth", "status"] },
        { "git fetch origin", ["git", "fetch", "origin"] },
        { "git -C /some/repo status", ["git", "status"] },
        { "git --no-pager log -1", ["git", "log"] },
    };

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [MemberData(nameof(Commands))]
    public async Task Everywhere_grant_saves_the_command_words(string command, string[] expectedWords)
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        var executable = command.Split(' ')[0];

        var stored = await ApproveEverywhereAsync(harness, command);

        var entry = Assert.Single(stored);
        Assert.Null(entry.Directory);
        Assert.Equal(expectedWords, entry.VerbTokens!);

        // Positive control: the saved grant covers the approved call.
        await AssertAllowedByStoredGrantAsync(harness, command);

        // Negative control: the saved grant does not cover an unrelated subcommand.
        foreach (var unrelated in executable == "gh" ? GhUnrelated : GitUnrelated)
            await AssertNeedsApprovalAsync(harness, unrelated, stored);
    }

    // Every option order of one command has the same command words, so one grant covers them all.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task One_grant_covers_every_option_order()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);

        var stored = await ApproveEverywhereAsync(harness, "gh pr comment 1 -R o/r --web");

        Assert.Equal(["gh", "pr", "comment"], Assert.Single(stored).VerbTokens!);
        await AssertAllowedByStoredGrantAsync(harness, "gh -R o/r pr comment 2");
        await AssertAllowedByStoredGrantAsync(harness, "gh pr -R o/r comment 3 --web");
        await AssertAllowedByStoredGrantAsync(harness, "gh --repo=o/r pr comment 4");
        await AssertNeedsApprovalAsync(harness, "gh pr merge 1", stored);
        await AssertNeedsApprovalAsync(harness, "gh -R o/r pr merge 1", stored);
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

    // A plain word is part of the identity: one branch grant does not cover another branch.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Branch_grant_does_not_cover_another_branch()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);

        var stored = await ApproveEverywhereAsync(harness, "git push origin feature-x");

        Assert.Equal(["git", "push", "origin", "feature-x"], Assert.Single(stored).VerbTokens!);
        await AssertNeedsApprovalAsync(harness, "git push origin main", stored);
        await AssertAllowedByStoredGrantAsync(harness, "git push origin feature-x --force-with-lease");
        // The position rule skips a word after an option, so "git push -f
        // origin main" has the words "git push main". It is not this grant.
        await AssertNeedsApprovalAsync(harness, "git push -f origin feature-x", stored);
    }

    // After the verb slot, an expansion or a brace list is an argument.
    // The command words stay known, so the grant for them covers the call.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("gh pr update-branch", "for n in 160 161; do gh pr update-branch $n; done")]
    [InlineData("dotnet build", "dotnet build -c Release")]
    public async Task Arguments_after_the_verb_slot_do_not_change_the_words(string grant, string command)
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere(grant));
        var stored = harness.GetStoredShellEntries(TrustAudience.Personal);

        await AssertAllowedByStoredGrantAsync(harness, command);
        await AssertNeedsApprovalAsync(harness, command.Replace(grant.Split(' ')[^1], "other-verb", StringComparison.Ordinal), stored);
    }

    // A word with a digit is an argument, so a tag grant covers every tag.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Legacy_exact_grant_matches_the_command_words()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        harness.AddStoredShellEntry(
            TrustAudience.Personal,
            ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "git push origin"));
        var stored = harness.GetStoredShellEntries(TrustAudience.Personal);

        await AssertAllowedByStoredGrantAsync(harness, "git push origin v1.5.1 --force");
        await AssertNeedsApprovalAsync(harness, "git push origin main --force", stored);
    }

    // A legacy phrase covers the calls whose command words equal it, whatever
    // the prompt shows. Other words still need approval.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("dotnet list package", "dotnet list package --vulnerable --include-transitive", "dotnet list reference")]
    [InlineData("git ls-remote", "git ls-remote --heads origin", "git ls-remote origin")]
    public async Task Legacy_exact_grant_covers_its_own_words(string grant, string covered, string other)
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        harness.AddStoredShellEntry(
            TrustAudience.Personal,
            ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, grant));
        var stored = harness.GetStoredShellEntries(TrustAudience.Personal);

        await AssertAllowedByStoredGrantAsync(harness, covered);
        await AssertNeedsApprovalAsync(harness, other, stored);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Single_token_program_grant_covers_its_options()
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("ls"));

        await AssertAllowedByStoredGrantAsync(harness, "ls -la");
    }

    // Unknown command words get a rewrite correction: no prompt and no run.
    // The corrected call then passes normal approval.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("rm *.md", ShellCommandWordsRewrite.UsePathGlob, "rm ./*.md", "Use ./* (a path with /)")]
    [InlineData("for v in push fetch; do git $v origin; done", ShellCommandWordsRewrite.WriteWordsLiterally, "git push origin", "Write the command words literally")]
    public async Task Unknown_command_words_get_a_rewrite_correction(
        string command,
        ShellCommandWordsRewrite expectedRewrite,
        string corrected,
        string expectedText)
    {
        var grantWords = corrected.Split(' ')[0] == "rm" ? "rm" : "git push origin";
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere(grantWords));

        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);

        Assert.Equal(ToolAuthorizationOutcome.RequiresAgentCorrection, decision.Outcome);
        Assert.Null(decision.ApprovalContext);
        var correction = Assert.IsType<ToolCorrection.ShellCommandWordsRewriteSuggested>(decision.AgentCorrection);
        Assert.Equal(expectedRewrite, correction.Rewrite);
        var delivery = ToolCorrectionDelivery.Create(new ToolCorrectionCollection([correction]), managedTemporaryCall: null);
        Assert.StartsWith("Tool execution deferred: rewrite_shell_command_words\n", delivery.Content, StringComparison.Ordinal);
        Assert.Contains(expectedText, delivery.Content, StringComparison.Ordinal);

        var run = await harness.RunShellAsync(command, Ct);
        Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, run.Outcome);
        Assert.Null(run.Output);

        await AssertAllowedByStoredGrantAsync(harness, corrected);
    }

    // A bare glob can expand to a subcommand: "git p?sh" with a file named
    // "push" runs "git push". A git grant must never run it.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Bare_glob_never_runs_under_a_program_grant()
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("git"));
        await File.WriteAllTextAsync(Path.Combine(harness.ProjectDirectory, "push"), string.Empty, Ct);

        var decision = await harness.EvaluateShellDecisionAsync("git p?sh", Ct);
        var run = await harness.RunShellAsync("git p?sh", Ct);

        Assert.Equal(ToolAuthorizationOutcome.RequiresAgentCorrection, decision.Outcome);
        Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, run.Outcome);
        Assert.Null(run.Output);
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
