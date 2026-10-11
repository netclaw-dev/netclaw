// -----------------------------------------------------------------------
// <copyright file="LongCommandCorrectionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// The operator must see the full command that they approve. A shell call that
/// would prompt with a command longer than
/// <see cref="ApprovalOptionKeys.MaxCommandTextChars"/> gets a correction:
/// no prompt, no denial, and no run. Allowed, denied, and unattended calls do
/// not change.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class LongCommandCorrectionTests(ShellApprovalMatrixFixture fixture)
{
    private const string Note = "note.txt";

    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // tee writes the note, so the command runs a program and prompts. The
    // prompt shows the full command, so the length of the command is the
    // length of the prompt text.
    private static string WriteNote(int commandLength)
    {
        const string head = "printf '%s' '";
        const string tail = "' | tee " + Note;
        return head + new string('b', commandLength - head.Length - tail.Length) + tail;
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Long_command_gets_a_correction_and_does_not_run()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None, interactive: true);
        var command = WriteNote(ApprovalOptionKeys.MaxCommandTextChars + 400);

        var first = await harness.RunShellAsync(command, Ct);
        var again = await harness.EvaluateShellDecisionAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, first.Outcome);
        Assert.Null(first.Output);
        Assert.False(File.Exists(Path.Combine(harness.ProjectDirectory, Note)));

        // A resend of the same call gets the same correction, never a prompt.
        Assert.Equal(ToolAuthorizationOutcome.RequiresAgentCorrection, again.Outcome);
        var correction = Assert.IsType<ToolCorrection.ShellCommandTooLongToShow>(again.AgentCorrection);
        Assert.Equal(command.Length, correction.Length);
        var delivery = ToolCorrectionDelivery.Create(new ToolCorrectionCollection([correction]), managedTemporaryCall: null);
        Assert.Equal(
            "Tool execution deferred: shorten_shell_command\n"
            + $"This command is too long to show for approval ({command.Length} characters, limit 900). "
            + "Write long text (a body, a script, file contents) to a file, then pass the file to the command "
            + "(for example `--body-file <file>` or `git commit -F <file>`). Then run the command again.",
            delivery.Content);
    }

    // The live trigger: a pull request body in a heredoc. Its exact candidate
    // verb is the full command text. Evaluated only: the command is never run.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Long_heredoc_pull_request_gets_a_correction()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None, interactive: true);

        var command = LongApprovalCommand.Command[LongApprovalCommand.Command.IndexOf("gh api", StringComparison.Ordinal)..];

        var observed = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, observed.Outcome);
        Assert.Equal(ApprovalCorrection.ShellCommandTooLongToShow, observed.AgentCorrection);
        Assert.Null(observed.Prompt);
    }

    // The threshold is the longest command that every channel shows in full.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(ApprovalOptionKeys.MaxCommandTextChars, false)]
    [InlineData(ApprovalOptionKeys.MaxCommandTextChars + 1, true)]
    public async Task Threshold_separates_a_prompt_from_a_correction(int length, bool corrected)
    {
        await using var harness = await CreateHarnessAsync(Approvals.None, interactive: true);

        var observed = await harness.EvaluateShellAsync(WriteNote(length), Ct);

        Assert.Equal(
            corrected ? ApprovalOutcome.RequiresAgentCorrection : ApprovalOutcome.RequiresApproval,
            observed.Outcome);
    }

    // The agent moved the body to a file. The short command gets a normal prompt.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Short_command_with_the_body_in_a_file_gets_a_prompt()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None, interactive: true);

        var observed = await harness.EvaluateShellAsync("cat body.txt > " + Note, Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        Assert.Equal("cat body.txt > " + Note, observed.Prompt!.DisplayText);
    }

    // The threshold must not add friction to a call that needs no prompt.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Long_command_that_a_grant_covers_runs()
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("touch"), interactive: true);
        var files = Enumerable.Range(0, 100).Select(static i => $"file-{i:D3}.txt").ToArray();
        var command = "touch " + string.Join(' ', files);
        Assert.True(command.Length > ApprovalOptionKeys.MaxCommandTextChars);

        var observed = await harness.EvaluateShellAsync(command, Ct);
        var run = await harness.RunShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, observed.AllowReason);
        Assert.Equal(ApprovalOutcome.Allowed, run.Outcome);
        Assert.All(files, file => Assert.True(File.Exists(Path.Combine(harness.ProjectDirectory, file))));
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Long_command_that_policy_allows_runs()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None, interactive: true);
        var text = new string('e', ApprovalOptionKeys.MaxCommandTextChars + 100);

        var run = await harness.RunShellAsync("echo " + text, Ct);

        Assert.Equal(ApprovalOutcome.Allowed, run.Outcome);
        Assert.Contains(text, run.Output, StringComparison.Ordinal);
    }

    // Evaluated only: a hard-denied command is never run.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Long_hard_denied_command_stays_denied()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None, interactive: true);

        var observed = await harness.EvaluateShellAsync(
            WriteNote(ApprovalOptionKeys.MaxCommandTextChars + 100) + " && netclaw daemon stop", Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal("hard_deny_self_destructive", observed.DenyReason);
    }

    // Nobody can answer a prompt in an unattended run, so the call keeps the
    // unattended denial.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Long_unattended_command_keeps_the_unattended_denial()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None, interactive: false);

        var observed = await harness.EvaluateShellAsync(WriteNote(ApprovalOptionKeys.MaxCommandTextChars + 400), Ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal(ToolAuthorizer.UnattendedApprovalRequired, observed.DenyReason);
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(ApprovalState approvals, bool interactive)
        => ShellApprovalHarness.CreateAsync(
            "long-command-correction",
            new ShellApprovalInvocation("true", Interactive: interactive),
            approvals,
            fixture.ActorSystem,
            Ct);
}
