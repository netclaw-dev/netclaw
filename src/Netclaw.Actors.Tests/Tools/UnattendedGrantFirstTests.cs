// -----------------------------------------------------------------------
// <copyright file="UnattendedGrantFirstTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Authorization PR 6e: an unattended call in Approval mode that stays denied
/// outside the trusted roots names each missing grant, also when the directory
/// proof of a ";" or "||" list decides.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class UnattendedGrantFirstTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "The Bash directory proof requires a POSIX host, as in the disposition matrix.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash directory proof requires a POSIX host.")]
    [InlineData("cd {X}; make")]
    [InlineData("cd {X} || exit 1; make")]
    public async Task A_directory_proof_denial_names_the_missing_grants(string template)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "grant-first-message",
            new ShellApprovalInvocation("true", Interactive: false),
            Approvals.None,
            fixture.ActorSystem,
            ct);
        var external = Path.Combine(Path.GetDirectoryName(harness.ProjectDirectory)!, "workspaces", "external");

        var observed = await harness.EvaluateShellAsync(template.Replace("{X}", external, StringComparison.Ordinal), ct);

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        Assert.Equal(1, observed.ApprovalChecks);
        Assert.Contains($"Missing grants: \"cd\" in {external}", observed.DenyMessage, StringComparison.Ordinal);
        Assert.Contains("\"make\"", observed.DenyMessage, StringComparison.Ordinal);
    }

    // After ";" or "||", the later command can also run in the working directory
    // (when cd fails). A grant for /x alone leaves that candidate uncovered.
    [SlopwatchSuppress("SW001", "The Bash directory proof requires a POSIX host, as in the disposition matrix.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash directory proof requires a POSIX host.")]
    [InlineData("cd {X}; make", new[] { "make" })]
    [InlineData("cd {X} || exit 1; make", new[] { "exit", "make" })]
    public async Task A_folder_grant_for_the_target_alone_names_the_working_directory_grant(string template, string[] later)
    {
        var observed = await EvaluateAsync(template, Approvals.PersistentHere(ApprovalDirectoryShape.External, ["cd", .. later]));

        Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
        foreach (var verb in later)
            Assert.Contains($"\"{verb}\" in {_projectDirectory}", observed.DenyMessage, StringComparison.Ordinal);
    }

    [SlopwatchSuppress("SW001", "The Bash directory proof requires a POSIX host, as in the disposition matrix.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash directory proof requires a POSIX host.")]
    [InlineData("cd {X}; make", new[] { "make" })]
    [InlineData("cd {X} || exit 1; make", new[] { "exit", "make" })]
    public async Task Folder_grants_for_each_directory_open_a_directory_proof_list(string template, string[] later)
    {
        var target = Approvals.PersistentHere(ApprovalDirectoryShape.External, ["cd", .. later]);
        var workingDirectory = Approvals.PersistentHere(ApprovalDirectoryShape.Project, later);

        var observed = await EvaluateAsync(template, new ApprovalState([.. target.Seeds, .. workingDirectory.Seeds]));

        Assert.True(observed.Outcome == ApprovalOutcome.Allowed, observed.DenyMessage);
        Assert.Equal(ApprovalAllowReason.StoredApproval, observed.AllowReason);
    }

    private string _projectDirectory = string.Empty;

    private async Task<ApprovalObservation> EvaluateAsync(string template, ApprovalState approvals)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "grant-first-folder",
            new ShellApprovalInvocation("true", Interactive: false),
            approvals,
            fixture.ActorSystem,
            ct);
        _projectDirectory = harness.ProjectDirectory;
        var external = Path.Combine(Path.GetDirectoryName(harness.ProjectDirectory)!, "workspaces", "external");
        return await harness.EvaluateShellAsync(template.Replace("{X}", external, StringComparison.Ordinal), ct);
    }
}
