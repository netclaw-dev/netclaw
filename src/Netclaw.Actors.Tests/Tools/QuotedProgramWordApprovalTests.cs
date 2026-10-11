// -----------------------------------------------------------------------
// <copyright file="QuotedProgramWordApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// A quoted program word with a space is a normal static word. Its value after
/// quote removal is its one identity: <c>"my tool"</c>, <c>'my tool'</c>, and
/// <c>my\ tool</c> name one program. A grant covers it, a program path names
/// its file (R1), and the prompt shows the word in quotes.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class QuotedProgramWordApprovalTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ToolApprovalSessionId OtherSession = (ToolApprovalSessionId)"signalr/other-session";

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("\"my tool\" --help", "'my tool'")]
    [InlineData("\"/opt/My App/bin/tool\" -v", "'/opt/My App/bin/tool'")]
    public async Task Quoted_program_with_a_space_gets_a_normal_prompt(string command, string shownVerb)
    {
        await using var harness = await CreateHarnessAsync();

        var decision = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        var prompt = Assert.IsType<ApprovalPromptObservation>(decision.Prompt);
        Assert.Equal([shownVerb], prompt.CandidateVerbs);
        Assert.Equal(command, prompt.DisplayText);
        Assert.False(prompt.IsMessy);
        Assert.Contains(ObservedOptionKeys.ApproveEverywhere, prompt.OptionKeys);
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Grant_covers_each_quoting_of_the_program_and_not_a_split_word()
    {
        await using var harness = await CreateHarnessAsync();

        var stored = await ApproveEverywhereAsync(harness, "\"my tool\" --help");

        var entry = Assert.Single(stored);
        Assert.Equal(["my tool"], entry.VerbTokens!);
        Assert.Equal("'my tool'", entry.Verb);
        await AssertAllowedByStoredGrantAsync(harness, "'my tool' --version");
        await AssertAllowedByStoredGrantAsync(harness, "my\\ tool -x");

        // Negative control: unquoted, the program is "my" and "tool" is a word.
        await AssertNeedsApprovalAsync(harness, "my tool --help");
    }

    // R1: a program path names its file. The absolute and the relative spelling
    // of a path with a space run one file, so one grant covers both.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Program_path_with_a_space_names_its_file()
    {
        await using var harness = await CreateHarnessAsync();
        var program = harness.ProjectDirectory + "/My App/bin/tool";

        var stored = await ApproveEverywhereAsync(harness, $"\"{program}\" -v");

        Assert.Equal([program], Assert.Single(stored).VerbTokens!);
        await AssertAllowedByStoredGrantAsync(harness, "\"./My App/bin/tool\" --version");
        await AssertAllowedByStoredGrantAsync(harness, "./My\\ App/bin/../bin/tool -v");

        // Negative control: another file in that folder needs its own grant.
        await AssertNeedsApprovalAsync(harness, "\"./My App/bin/other\" -v");
    }

    // SECURITY: the phrase of the program "echo x" is 'echo x', not the phrase
    // of the side-effect verb echo. Such a call is not approval-exempt.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Program_with_a_space_does_not_read_as_its_first_word()
    {
        await using var harness = await CreateHarnessAsync();

        var exempt = await harness.EvaluateShellAsync("echo x hi", Ct);
        var quoted = await harness.EvaluateShellAsync("\"echo x\" hi", Ct);

        Assert.Equal(ApprovalOutcome.Allowed, exempt.Outcome);
        Assert.Equal(ApprovalOutcome.RequiresApproval, quoted.Outcome);
        Assert.Equal(["'echo x' hi"], quoted.Prompt!.CandidateVerbs);
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync()
        => ShellApprovalHarness.CreateAsync(
            "quoted-program-word",
            new ShellApprovalInvocation("true"),
            Approvals.None,
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

        var grants = GrantBuilder.Build(
            approval.Candidates!,
            GrantScopeKind.Everywhere,
            approval.Cwd,
            harness.SessionDirectory,
            approval.RepositoryCommonDirectory);
        await harness.ApprovalService.RecordApprovalCandidatesAsync(
            OtherSession,
            TrustAudience.Personal,
            new ToolName(ShellTool.ToolName),
            grants,
            Ct);
        return harness.GetStoredShellEntries(TrustAudience.Personal);
    }

    private static async Task AssertAllowedByStoredGrantAsync(ShellApprovalHarness harness, string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.Allowed
            && decision.AllowReason == ToolAllowReason.StoredApproval,
            $"'{command}' was {decision.Outcome} ({decision.AllowReason}) {decision.DenyReason}; a stored grant should cover it.");
    }

    private static async Task AssertNeedsApprovalAsync(ShellApprovalHarness harness, string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.RequiresApproval,
            $"'{command}' was {decision.Outcome} ({decision.AllowReason}) {decision.DenyReason}.");
    }
}
