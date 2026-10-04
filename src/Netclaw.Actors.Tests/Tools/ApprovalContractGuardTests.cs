// -----------------------------------------------------------------------
// <copyright file="ApprovalContractGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Guards for authority rules that had no test before the authorization
/// consolidation. Each case pins current behavior through the production
/// registration and the tool executor.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ApprovalContractGuardTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    public static bool IsMacOS => OperatingSystem.IsMacOS();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Guard: a shell grant never authorizes file_read. Grants stay keyed by
    // audience and tool. The operator makes file_read ask for consent. A
    // persistent repository grant for "cat" in repository A covers a shell
    // read in A, but file_read of the same file still asks for consent.
    [SlopwatchSuppress("SW001", "The repository uses POSIX paths and the git CLI.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The repository uses POSIX paths and the git CLI.")]
    public async Task Shell_grant_does_not_authorize_file_read()
    {
        var root = ApprovalTestGit.CreateRoot("netclaw-shell-grant-file-read-");
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(root.FullName, "repository-a")).FullName;
            var session = Directory.CreateDirectory(Path.Combine(root.FullName, "session")).FullName;
            var project = Directory.CreateDirectory(Path.Combine(root.FullName, "project")).FullName;
            await ApprovalTestGit.CreateRepositoryAsync(repository);
            var secret = Path.Combine(repository, "secret.txt");
            await File.WriteAllTextAsync(secret, "repository-secret-content", Ct);
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "shell-grant-file-read",
                new ShellApprovalInvocation("true"),
                Approvals.PersistentRepository("cat"),
                fixture.ActorSystem,
                Ct,
                scope: new ShellApprovalHarnessScope(project, session, "signalr/shell-grant-file-read", [])
                {
                    RepositoryGrantWorktree = repository
                },
                policy: new ShellApprovalHarnessPolicy
                {
                    PersonalApprovalOverrides = new Dictionary<string, ToolApprovalMode>
                    {
                        ["file_read"] = ToolApprovalMode.Approval
                    }
                });

            // Control: the grant is live. It covers the shell read in A.
            var shell = await harness.EvaluateShellAsync("cat secret.txt", Ct, repository);
            Assert.Equal(ApprovalOutcome.Allowed, shell.Outcome);
            Assert.Equal(ApprovalAllowReason.StoredApproval, shell.AllowReason);

            var read = await harness.EvaluateToolAsync("file_read", ToolInput.Create("Path", secret), Ct);
            var run = await harness.RunToolAsync("file_read", ToolInput.Create("Path", secret), Ct);

            Assert.Equal(ApprovalOutcome.RequiresApproval, read.Outcome);
            Assert.Null(read.AllowReason);
            Assert.True(read.ApprovalChecks >= 1, "The file_read grant lookup must run.");
            Assert.Empty(read.ApprovalMatches);
            Assert.Equal(ApprovalOutcome.RequiresApproval, run.Outcome);
            Assert.Null(run.Output);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // Guard: file tools treat "~/x" as a literal relative path under the
    // project. This pins current behavior. It is not a judgment that the
    // behavior is right.
    [Fact]
    public async Task File_tool_keeps_a_tilde_path_under_the_project()
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "file-tool-tilde",
            new ShellApprovalInvocation("true"),
            Approvals.None,
            fixture.ActorSystem,
            Ct);
        var literal = Path.Combine(harness.ProjectDirectory, "~", "x");
        Directory.CreateDirectory(Path.GetDirectoryName(literal)!);
        await File.WriteAllTextAsync(literal, "project-tilde-content", Ct);

        var run = await harness.RunToolAsync("file_read", ToolInput.Create("Path", "~/x"), Ct);

        Assert.Equal(ApprovalOutcome.Allowed, run.Outcome);
        Assert.Contains("project-tilde-content", run.Output);
    }

    // Guard: the "file_write:control-plane" approval override key has no
    // effect with the production protected paths. The configuration directory
    // is write-denied before the approval mode is read.
    [Theory]
    [InlineData(ToolApprovalMode.Auto)]
    [InlineData(ToolApprovalMode.Approval)]
    public async Task Control_plane_approval_override_does_not_open_the_configuration_directory(ToolApprovalMode mode)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "control-plane-override",
            new ShellApprovalInvocation("true"),
            Approvals.None,
            fixture.ActorSystem,
            Ct,
            policy: new ShellApprovalHarnessPolicy
            {
                PersonalApprovalOverrides = new Dictionary<string, ToolApprovalMode>
                {
                    ["file_write:control-plane"] = mode
                }
            });
        var target = harness.Paths.NetclawConfigPath;
        var arguments = ToolInput.Create("Path", target, "Content", "{}");

        var decision = await harness.EvaluateToolAsync("file_write", arguments, Ct);
        var run = await harness.RunToolAsync("file_write", arguments, Ct);

        Assert.Equal(ApprovalOutcome.Denied, decision.Outcome);
        Assert.Equal("path_access_denied", decision.DenyReason);
        Assert.Equal(ApprovalOutcome.Denied, run.Outcome);
        Assert.False(File.Exists(target));
    }

    // Guard: allow checks compare paths with case. On a case-insensitive macOS
    // volume, a path that differs from the project only in case names the same
    // files, but it is not inside the project for authorization.
    [SlopwatchSuppress("SW001", "The case needs a case-insensitive macOS volume.")]
    [Fact(SkipUnless = nameof(IsMacOS), Skip = "The case needs a case-insensitive macOS volume.")]
    public async Task Allow_checks_compare_paths_with_case_on_macos()
    {
        await using var unattended = await ShellApprovalHarness.CreateAsync(
            "macos-case-unattended",
            new ShellApprovalInvocation("true", Interactive: false),
            Approvals.None,
            fixture.ActorSystem,
            Ct);
        var exact = Path.Combine(unattended.ProjectDirectory, "a.txt");
        await File.WriteAllTextAsync(exact, "project data", Ct);
        var variant = Path.Combine(
            Path.GetDirectoryName(unattended.ProjectDirectory)!,
            Path.GetFileName(unattended.ProjectDirectory).ToUpperInvariant(),
            "a.txt");

        var exactRead = await unattended.EvaluateToolAsync("file_read", ToolInput.Create("Path", exact), Ct);
        var variantRead = await unattended.EvaluateToolAsync("file_read", ToolInput.Create("Path", variant), Ct);

        Assert.Equal(ApprovalOutcome.Allowed, exactRead.Outcome);
        Assert.Equal(ApprovalOutcome.Denied, variantRead.Outcome);
        Assert.Equal("path_access_denied", variantRead.DenyReason);

        // An interactive reviewed phrase may read each path that the profile
        // reads. Confine reads, so the reviewed-safe root check decides.
        await using var interactive = await ShellApprovalHarness.CreateAsync(
            "macos-case-interactive",
            new ShellApprovalInvocation("true"),
            Approvals.None,
            fixture.ActorSystem,
            Ct,
            policy: new ShellApprovalHarnessPolicy
            {
                ConfigureTools = config =>
                {
                    config.AudienceProfiles.GlobalReadRoots = [];
                    config.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
                    {
                        Mode = ToolFilesystemMode.Roots,
                        Roots = []
                    };
                }
            });
        var project = Path.Combine(interactive.ProjectDirectory, "a.txt");
        await File.WriteAllTextAsync(project, "project data", Ct);
        var projectVariant = Path.Combine(
            Path.GetDirectoryName(interactive.ProjectDirectory)!,
            Path.GetFileName(interactive.ProjectDirectory).ToUpperInvariant(),
            "a.txt");

        var exactShell = await interactive.EvaluateShellAsync($"cat '{project}'", Ct);
        var variantShell = await interactive.EvaluateShellAsync($"cat '{projectVariant}'", Ct);

        Assert.Equal(ApprovalOutcome.Allowed, exactShell.Outcome);
        Assert.Equal(ApprovalOutcome.RequiresApproval, variantShell.Outcome);
    }
}
