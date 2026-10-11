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
/// A saved shell grant covers the command words (the ShellSyntaxTree
/// <c>CommandWords</c> fact) that start with its words. A grant with two or
/// more words names a verb, and the later words are its arguments. A grant
/// with one word names only the program and stays exact: a <c>gh</c> grant
/// covers <c>gh --help</c>, not <c>gh auth logout</c>. Every option order of
/// one command has the same words. After the verb slot, option values,
/// expansions, and globs are arguments. Unknown words (only in the verb slot)
/// get a rewrite correction (no run and no prompt) when the source holds
/// their literal form. A word with a run-time value gets a one-time prompt.
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

    // The owner decision of 2026-10-05: a verb grant covers its command words
    // and any later words, and a program-only grant stays exact. A word of the
    // grant is never free. Both stored kinds follow the same rule.
    public static TheoryData<ApprovalMatchKind, string, string, bool> OwnerReachCases()
    {
        var data = new TheoryData<ApprovalMatchKind, string, string, bool>();
        (string Grant, string Command, bool Covered)[] cases =
        [
            ("dotnet package search", "dotnet package search Dapper.AOT", true),
            ("dotnet package search", "dotnet package search Newtonsoft.Json --take 5", true),
            ("git push", "git push upstream", true),
            ("git push", "git push origin main", true),
            ("git push upstream", "git push upstream feature-x", true),
            ("git push upstream", "git push origin main", false),
            ("git push origin feature-x", "git push origin main", false),
            ("gh", "gh auth logout", false),
            ("ilspycmd Mattermost.MattermostClient", "ilspycmd Mattermost.MattermostClient", true),
            ("ilspycmd Mattermost.MattermostClient", "ilspycmd Mattermost.MattermostClient Extra.Word", true),
            ("ilspycmd Mattermost.MattermostClient", "ilspycmd Other.Type", false),
        ];
        foreach (var kind in new[] { ApprovalMatchKind.TokenPrefix, ApprovalMatchKind.LegacyExact })
        {
            foreach (var (grant, command, covered) in cases)
                data.Add(kind, grant, command, covered);
        }

        return data;
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [MemberData(nameof(OwnerReachCases))]
    public async Task Verb_grant_covers_its_arguments_and_program_grant_stays_exact(
        ApprovalMatchKind kind,
        string grant,
        string command,
        bool covered)
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        harness.AddStoredShellEntry(
            TrustAudience.Personal,
            kind == ApprovalMatchKind.TokenPrefix
                ? ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, grant.Split(' '))
                : ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, grant));
        var stored = harness.GetStoredShellEntries(TrustAudience.Personal);

        if (covered)
            await AssertAllowedByStoredGrantAsync(harness, command);
        else
            await AssertNeedsApprovalAsync(harness, command, stored);
    }

    // Saving does not change: "Always anywhere" stores the words of the
    // approved call, so a grant for one remote stays narrow.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Saved_grant_keeps_the_approved_words()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);

        var stored = await ApproveEverywhereAsync(harness, "git push upstream");

        Assert.Equal(["git", "push", "upstream"], Assert.Single(stored).VerbTokens!);
        await AssertAllowedByStoredGrantAsync(harness, "git push upstream feature-x");
        await AssertNeedsApprovalAsync(harness, "git push origin main", stored);
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

    // A legacy phrase covers its words and later words. A word with a digit is
    // an argument, and "main" is a later word.
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
        await AssertAllowedByStoredGrantAsync(harness, "git push origin main --force");
        await AssertNeedsApprovalAsync(harness, "git push upstream main", stored);
    }

    // A legacy phrase covers the calls whose command words start with it,
    // whatever the prompt shows. Other words still need approval.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("dotnet list package", "dotnet list package --vulnerable --include-transitive", "dotnet list reference")]
    [InlineData("git ls-remote", "git ls-remote --heads origin", "git remote prune origin")]
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
    // ShellSyntaxTree 0.4.0-beta.18 gives a brace word an unknown value with no
    // public cause, so the advice is the general expansion advice.
    [InlineData("git {push,fetch} origin", ShellCommandWordsRewrite.WriteWordsLiterally, "git push origin", "Write the command words literally")]
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

    // F4: the command words are known, and only an unquoted word with an
    // unknown value makes the command exact. The model gets a quote
    // correction that names the word: no prompt and no run. The quoted retry
    // has one unknown operand, so the verb grant covers it (decision D1).
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(
        "git rev-list --count HEAD...origin/$(git branch --show-current)",
        "HEAD...origin/$(git branch --show-current)",
        "Put the word in double quotes: \"HEAD...origin/$(git branch --show-current)\".",
        "git rev-list --count \"HEAD...origin/$(git branch --show-current)\"")]
    [InlineData(
        "git rev-list --count origin/$(git branch --show-current)'^'",
        "origin/$(git branch --show-current)'^'",
        "Put each expansion in that word in double quotes.",
        "git rev-list --count origin/\"$(git branch --show-current)\"'^'")]
    public async Task Known_words_with_an_unquoted_expansion_get_a_quote_correction(
        string command,
        string expectedWord,
        string expectedAdvice,
        string corrected)
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentAnywhere("git rev-list", "git branch"));

        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);

        Assert.Equal(ToolAuthorizationOutcome.RequiresAgentCorrection, decision.Outcome);
        Assert.Null(decision.ApprovalContext);
        var correction = Assert.IsType<ToolCorrection.ShellWordQuoteSuggested>(decision.AgentCorrection);
        Assert.Equal([expectedWord], correction.Words);
        var delivery = ToolCorrectionDelivery.Create(new ToolCorrectionCollection([correction]), managedTemporaryCall: null);
        Assert.StartsWith("Tool execution deferred: rewrite_shell_command_words\n", delivery.Content, StringComparison.Ordinal);
        Assert.Contains(expectedAdvice, delivery.Content, StringComparison.Ordinal);

        var run = await harness.RunShellAsync(command, Ct);
        Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, run.Outcome);
        Assert.Null(run.Output);

        await AssertAllowedByStoredGrantAsync(harness, corrected);
    }

    // The kind is a ShellCommandWordsRewrite name, or QuoteKind for the quote
    // correction. {home} is the home directory of the test process.
    private const string QuoteKind = "ShellWordQuote";

    public static TheoryData<string, string, string, string, string> CorrectionRewrites()
        => new()
        {
            { "Bash", nameof(ShellCommandWordsRewrite.UsePathGlob), "rm *.md", "rm ./*.md", "" },
            { "PowerShell7", nameof(ShellCommandWordsRewrite.UsePathGlob), "rm *.md", "rm ./*.md", "" },
            { "Bash52", nameof(ShellCommandWordsRewrite.WriteWordsLiterally), "git {push,fetch} origin", "git push origin; git fetch origin", "" },
            { "Bash", nameof(ShellCommandWordsRewrite.WriteWordsLiterally), "for v in push fetch; do git $v origin; done", "git push origin; git fetch origin", "" },
            // A proved value with a glob character still has a literal form.
            { "Bash52", nameof(ShellCommandWordsRewrite.WriteWordsLiterally), "for f in '*.cs'; do cat /work/$f; done", "cat '/work/*.cs'", "" },
            // The retry is denied: each literal path is a credential path.
            { "Bash52", nameof(ShellCommandWordsRewrite.WriteWordsLiterally), "cat ~/.netclaw/{keys,config}/key-1.xml", "cat ~/.netclaw/keys/key-1.xml; cat ~/.netclaw/config/key-1.xml", "" },
            // A run-time operand after the cause does not decide. The brace
            // list is the cause, and the grants cover the literal rewrite.
            { "Bash52", nameof(ShellCommandWordsRewrite.WriteWordsLiterally), "git {push,fetch} origin \"$BRANCH\"", "git push origin \"$BRANCH\"; git fetch origin \"$BRANCH\"", "git push,git fetch" },
            // A run-time assignment that the command does not read does not decide.
            { "Bash52", nameof(ShellCommandWordsRewrite.WriteWordsLiterally), "x=$(date); git {push,fetch} origin", "x=$(date); git push origin; git fetch origin", "" },
            { "Bash", nameof(ShellCommandWordsRewrite.RunCommandsSeparately), "git $'pu\\x73h' origin", "git push origin", "" },
            { "Bash", nameof(ShellCommandWordsRewrite.WriteProgramPathInFull), "~/bin/tool run", "{home}/bin/tool run", "" },
            { "Bash52", QuoteKind, "git rev-list --count HEAD...origin/$(git branch --show-current)", "git rev-list --count \"HEAD...origin/$(git branch --show-current)\"", "" },
            { "Bash52", QuoteKind, "f=$(date); git log origin/$f", "f=$(date); git log \"origin/$f\"", "" },
        };

    // Owner decision (2026-10-07): a correction is sent only when a rewrite
    // that the model can make removes the cause. Each command-words and quote
    // correction has a row: the advised rewrite gets no correction on the
    // retry. The retry can still prompt or be denied. A row with grants also
    // proves that the grants cover the rewrite.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [MemberData(nameof(CorrectionRewrites))]
    public async Task Each_correction_has_a_rewrite_that_removes_it(
        string host,
        string kind,
        string command,
        string rewritten,
        string grants)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var approvals = grants.Length == 0 ? Approvals.None : Approvals.PersistentAnywhere(grants.Split(','));
        await using var harness = await CreateHarnessAsync(approvals, Enum.Parse<ShellApprovalHost>(host));

        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        var retry = await harness.EvaluateShellDecisionAsync(rewritten.Replace("{home}", home, StringComparison.Ordinal), Ct);

        var actualKind = decision.AgentCorrection switch
        {
            ToolCorrection.ShellCommandWordsRewriteSuggested words => words.Rewrite.ToString(),
            ToolCorrection.ShellWordQuoteSuggested => QuoteKind,
            var other => $"unexpected: {other}"
        };
        Assert.Equal(kind, actualKind);
        Assert.NotEqual(ToolAuthorizationOutcome.RequiresAgentCorrection, retry.Outcome);
        if (grants.Length > 0)
            Assert.Equal(ToolAuthorizationOutcome.Allowed, retry.Outcome);
    }

    // A new rewrite kind without a row in CorrectionRewrites fails here.
    [Fact]
    public void Each_rewrite_kind_has_a_rewrite_row()
    {
        var kinds = CorrectionRewrites().Select(static row => row.Data.Item2).ToHashSet(StringComparer.Ordinal);

        Assert.All(Enum.GetNames<ShellCommandWordsRewrite>(), name => Assert.Contains(name, kinds));
        Assert.Contains(QuoteKind, kinds);
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

    private Task<ShellApprovalHarness> CreateHarnessAsync(
        ApprovalState approvals,
        ShellApprovalHost host = ShellApprovalHost.Bash)
        => ShellApprovalHarness.CreateAsync(
            "everywhere-subcommand-grant",
            new ShellApprovalInvocation("true", Host: host),
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
