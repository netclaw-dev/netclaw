// -----------------------------------------------------------------------
// <copyright file="UnattendedSamePolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Decision D2: an unattended run uses the same audience policy as a chat. The
/// one difference: nobody can answer a consent request in an unattended run,
/// so a call that would prompt in a chat is denied.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class UnattendedSamePolicyTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "The Bash directory proof and the reviewed phrases require a POSIX host, as in the disposition matrix.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash directory proof requires a POSIX host.")]
    [InlineData("cd {X}; make")]
    [InlineData("cd {X} || exit 1; make")]
    [InlineData("cat {X}/notes.txt")]
    [InlineData("git -C {X} status")]
    [InlineData("touch {X}/marker")]
    [InlineData("ls {X} | grep $(whoami)")]
    [InlineData("netclaw daemon stop")]
    [InlineData("cat ~/.netclaw/config/secrets.json")]
    public async Task An_unattended_call_gets_the_attended_decision_or_a_denied_prompt(string template)
    {
        var attended = await EvaluateAsync(template, Approvals.None, interactive: true);
        var unattended = await EvaluateAsync(template, Approvals.None, interactive: false);

        if (attended.Outcome == ApprovalOutcome.RequiresApproval)
        {
            Assert.Equal(ApprovalOutcome.Denied, unattended.Outcome);
            Assert.Equal(ToolAuthorizer.UnattendedApprovalRequired, unattended.DenyReason);
            return;
        }

        Assert.Equal(attended.Outcome, unattended.Outcome);
        Assert.Equal(attended.AllowReason, unattended.AllowReason);
        Assert.Equal(attended.DenyReason, unattended.DenyReason);
    }

    // After ";" or "||", the later command can also run in the working directory
    // (when cd fails). A grant for the target alone leaves that candidate uncovered.
    [SlopwatchSuppress("SW001", "The Bash directory proof requires a POSIX host, as in the disposition matrix.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash directory proof requires a POSIX host.")]
    [InlineData("cd {X}; make", new[] { "make" })]
    [InlineData("cd {X} || exit 1; make", new[] { "exit", "make" })]
    public async Task A_folder_grant_for_the_target_alone_leaves_the_call_denied(string template, string[] later)
    {
        var observed = await EvaluateAsync(
            template,
            Approvals.PersistentHere(ApprovalDirectoryShape.External, ["cd", .. later]),
            interactive: false);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal(ToolAuthorizer.UnattendedApprovalRequired, observed.DenyReason);
    }

    [SlopwatchSuppress("SW001", "The Bash directory proof requires a POSIX host, as in the disposition matrix.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash directory proof requires a POSIX host.")]
    [InlineData("cd {X}; make", new[] { "make" })]
    [InlineData("cd {X} || exit 1; make", new[] { "exit", "make" })]
    public async Task Folder_grants_for_each_directory_let_an_unattended_list_run(string template, string[] later)
    {
        var target = Approvals.PersistentHere(ApprovalDirectoryShape.External, ["cd", .. later]);
        var workingDirectory = Approvals.PersistentHere(ApprovalDirectoryShape.Project, later);

        var observed = await EvaluateAsync(
            template,
            new ApprovalState([.. target.Seeds, .. workingDirectory.Seeds]),
            interactive: false);

        Assert.True(observed.Outcome == ApprovalOutcome.Allowed, observed.DenyMessage);
        Assert.Equal(ApprovalAllowReason.StoredApproval, observed.AllowReason);
    }

    private async Task<ApprovalObservation> EvaluateAsync(string template, ApprovalState approvals, bool interactive)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "same-policy",
            new ShellApprovalInvocation("true", Interactive: interactive),
            approvals,
            fixture.ActorSystem,
            ct);
        var external = Path.Combine(Path.GetDirectoryName(harness.ProjectDirectory)!, "workspaces", "external");
        Directory.CreateDirectory(external);
        return await harness.EvaluateShellAsync(template.Replace("{X}", external, StringComparison.Ordinal), ct);
    }
}
