// -----------------------------------------------------------------------
// <copyright file="OptionValueScopeApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// The value of an inline option (<c>--name=value</c>) can name a path for the
/// program. A folder or a repository grant covers the command only when each
/// such path is in the grant scope (#2364). The catalog rows in
/// <see cref="ShellApprovalCases"/> hold the folder, chat, and anywhere grants.
/// These rows need a Git repository or a link.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class OptionValueScopeApprovalTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [SlopwatchSuppress("SW001", "The repository uses POSIX paths and the git CLI.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The repository uses POSIX paths and the git CLI.")]
    public async Task Repository_grant_does_not_cover_an_option_value_outside_the_repository()
    {
        var root = ApprovalTestGit.CreateRoot("netclaw-option-value-repository-");
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(root.FullName, "repository")).FullName;
            var session = Directory.CreateDirectory(Path.Combine(root.FullName, "session")).FullName;
            await ApprovalTestGit.CreateRepositoryAsync(repository);
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "option-value-repository-grant",
                new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
                Approvals.PersistentRepository("dotnet build"),
                fixture.ActorSystem,
                Ct,
                scope: new ShellApprovalHarnessScope(repository, session, "signalr/option-value-repository-grant", [])
                {
                    RepositoryGrantWorktree = repository
                });

            // Controls: the grant covers a value that is not a path, and a
            // value inside the repository.
            foreach (var control in new[]
                     {
                         "dotnet build --configuration=Release",
                         "dotnet build --output=bin/x",
                     })
            {
                var allowed = await harness.EvaluateShellAsync(control, Ct);
                Assert.True(
                    allowed.Outcome == ApprovalOutcome.Allowed
                    && allowed.AllowReason == ApprovalAllowReason.StoredApproval,
                    $"{control}: {allowed.Outcome} {allowed.AllowReason}");
            }

            foreach (var command in new[]
                     {
                         "dotnet build --output=../outside/x",
                         "dotnet build --output=$HOME/x",
                         "dotnet build --output=/etc/x",
                     })
            {
                var decision = await harness.EvaluateShellAsync(command, Ct);

                Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
                var prompt = Assert.IsType<ApprovalPromptObservation>(decision.Prompt);
                Assert.Equal(["dotnet build"], prompt.CandidateVerbs);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // SECURITY: a value below a link in the folder names a path outside the
    // folder. It gets the rule of a path word with the same text: a scope
    // that the folder grant does not cover.
    [SlopwatchSuppress("SW001", "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case uses a POSIX symbolic link and Bash authorization behavior.")]
    public async Task Folder_grant_does_not_cover_an_option_value_below_a_link()
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "option-value-link-folder-grant",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "dotnet build"),
            fixture.ActorSystem,
            Ct);
        // project/lnk -> workspaces/external, outside the project.
        harness.ReplaceProjectDirectoryWithExternalSymlink("lnk");

        var decision = await harness.EvaluateShellAsync("dotnet build --output=lnk/x", Ct);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
    }

    // A list "cd dir && action; read" is a causal list only when each scope
    // of the read is below "dir". An option value outside "dir" is a scope,
    // so the inline form gets the decision of its path word form. Before
    // #2364 the matcher did not read the value, and the inline form prompted.
    [SlopwatchSuppress("SW001", "The case uses POSIX paths and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The case uses POSIX paths and Bash authorization behavior.")]
    [InlineData("du --exclude-from={T}/patterns ./data")]
    [InlineData("du -X {T}/patterns ./data")]
    public async Task Read_after_a_directory_change_gets_one_result_for_both_option_forms(string read)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "option-value-causal-list",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "cd", "git fetch"),
            fixture.ActorSystem,
            Ct);
        harness.CreateProjectDirectory("sub");
        var command = $"cd {harness.ProjectDirectory}/sub && git fetch; "
                      + read.Replace("{T}", harness.ManagedTemporaryDirectory, StringComparison.Ordinal);

        var decision = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Allowed, decision.Outcome);
    }
}
