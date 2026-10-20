// -----------------------------------------------------------------------
// <copyright file="ManagedScratchApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ManagedScratchApprovalTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [SlopwatchSuppress("SW001", "Scratch composition currently supports Personal Bash calls on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash paths.")]
    public async Task Repository_grant_composes_with_owned_scratch_but_preserves_external_scopes()
    {
        using var root = new DisposableTempDir();
        var project = InitializeRepository(root.Path);
        await using var harness = await CreateHarnessAsync(root.Path, project, "first", ToolApprovalMode.Auto, ToolApprovalMode.Approval);
        await using var other = await CreateHarnessAsync(root.Path, project, "other", ToolApprovalMode.Auto, ToolApprovalMode.Approval);
        Directory.CreateDirectory(harness.ManagedTemporaryDirectory);
        Directory.CreateDirectory(other.ManagedTemporaryDirectory);

        var owned = await harness.EvaluateShellAsync("uv export --frozen > \"$TMPDIR/reqs.txt\"", Ct, project);
        Assert.True(owned.Outcome == ApprovalOutcome.Allowed, System.Text.Json.JsonSerializer.Serialize(owned));
        Assert.Contains(owned.ApprovalMatches, match => match.Contains("uv export", StringComparison.Ordinal));

        var external = Path.Combine(root.Path, "external.txt");
        var externalRedirect = await harness.EvaluateShellAsync($"uv export > '{external}'", Ct, project);
        Assert.NotEqual(ApprovalOutcome.Allowed, externalRedirect.Outcome);
        var externalOperand = await harness.EvaluateShellAsync($"uv export '{external}' > \"$TMPDIR/reqs.txt\"", Ct, project);
        Assert.NotEqual(ApprovalOutcome.Allowed, externalOperand.Outcome);
        var foreign = await harness.EvaluateShellAsync($"uv export > '{other.ManagedTemporaryDirectory}/reqs.txt'", Ct, project);
        Assert.NotEqual(ApprovalOutcome.Allowed, foreign.Outcome);

        var link = Path.Combine(harness.ManagedTemporaryDirectory, "escape");
        File.CreateSymbolicLink(link, external);
        var linked = await harness.EvaluateShellAsync($"uv export > '{link}'", Ct, project);
        Assert.NotEqual(ApprovalOutcome.Allowed, linked.Outcome);
    }

    [SlopwatchSuppress("SW001", "Scratch composition currently supports Personal Bash calls on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash paths.")]
    [InlineData(ToolApprovalMode.Auto, ToolApprovalMode.Approval, "Allowed")]
    [InlineData(ToolApprovalMode.Deny, ToolApprovalMode.Approval, "Denied")]
    [InlineData(ToolApprovalMode.Approval, ToolApprovalMode.Approval, "RequiresApproval")]
    [InlineData(ToolApprovalMode.Deny, ToolApprovalMode.Auto, "Denied")]
    [InlineData(ToolApprovalMode.Approval, ToolApprovalMode.Auto, "RequiresApproval")]
    public async Task Scratch_redirect_obeys_file_consent_even_with_shell_authority(
        ToolApprovalMode fileMode, ToolApprovalMode shellMode, string expected)
    {
        using var root = new DisposableTempDir();
        var project = InitializeRepository(root.Path);
        await using var harness = await CreateHarnessAsync(root.Path, project, "file-mode", fileMode, shellMode);
        Directory.CreateDirectory(harness.ManagedTemporaryDirectory);
        var result = await harness.EvaluateShellAsync("uv export > \"$TMPDIR/reqs.txt\"", Ct, project);
        Assert.Equal(Enum.Parse<ApprovalOutcome>(expected), result.Outcome);
        if (expected == "RequiresApproval")
        {
            harness.AddStoredEntry(TrustAudience.Personal, FileWriteTool.ToolName, new ApprovalEntry(FileWriteTool.ToolName));
            var granted = await harness.EvaluateShellAsync("uv export > \"$TMPDIR/reqs.txt\"", Ct, project);
            Assert.Equal(ApprovalOutcome.Allowed, granted.Outcome);
        }
    }

    [SlopwatchSuppress("SW001", "Scratch composition currently supports Personal Bash calls on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash paths.")]
    public async Task Project_output_gets_advice_and_the_replacement_passes_the_full_gate()
    {
        using var root = new DisposableTempDir();
        var project = InitializeRepository(root.Path);
        await using var harness = await CreateHarnessAsync(root.Path, project, "advice", ToolApprovalMode.Auto, ToolApprovalMode.Approval);
        var shared = Path.Combine(Path.GetTempPath(), "audit-reqs.txt");
        var advised = await harness.EvaluateShellAsync($"uv export > '{shared}'", Ct, project);
        Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, advised.Outcome);
        Assert.Equal(harness.ManagedTemporaryDirectory.TrimEnd('/'), advised.AgentCorrectionTarget!.TrimEnd('/'));
        var input = await harness.EvaluateShellAsync($"uv export < '{shared}' > '{shared}.out'", Ct, project);
        Assert.Equal(ApprovalOutcome.RequiresApproval, input.Outcome);
        var corrected = await harness.EvaluateShellAsync("uv export > \"$TMPDIR/reqs.txt\"", Ct, project);
        Assert.Equal(ApprovalOutcome.Allowed, corrected.Outcome);
    }

    [SlopwatchSuppress("SW001", "Scratch composition currently supports Personal Bash calls on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash paths.")]
    public async Task Consent_explains_program_reach_and_a_grant_mismatch_without_changing_candidates()
    {
        using var root = new DisposableTempDir();
        var project = InitializeRepository(root.Path);
        await using var harness = await CreateHarnessAsync(root.Path, project, "reach", ToolApprovalMode.Auto, ToolApprovalMode.Approval);
        var bare = await harness.EvaluateShellDecisionAsync("uv --version", Ct);
        Assert.Contains("does not cover subcommands", bare.ApprovalContext!.DisplayText, StringComparison.Ordinal);
        var mismatch = await harness.EvaluateShellDecisionAsync("uv run pytest", Ct);
        Assert.Contains("different command words", mismatch.ApprovalContext!.DisplayText, StringComparison.Ordinal);
        Assert.Equal(["uv", "run", "pytest"], Assert.Single(mismatch.ApprovalContext!.Candidates!).VerbTokens);
    }

    [SlopwatchSuppress("SW001", "Scratch composition currently supports Personal Bash calls on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash paths.")]
    public async Task Native_launch_preserves_the_repository_directory_and_creates_only_the_owned_output()
    {
        using var root = new DisposableTempDir();
        var project = InitializeRepository(root.Path);
        await using var harness = await CreateHarnessAsync(root.Path, project, "launch", ToolApprovalMode.Auto, ToolApprovalMode.Approval);
        harness.AddStoredShellEntry(TrustAudience.Personal,
            ApprovalEntry.CreateRepositoryTokenPrefix(ApprovalShell.Bash, ["git"], Path.Combine(project, ".git")));
        var result = await harness.RunToolAsync(ShellTool.ToolName, new Dictionary<string, object?>
        {
            ["Command"] = "git --version > \"$TMPDIR/version.txt\"",
            ["WorkingDirectory"] = project
        }, Ct);
        Assert.Equal(ApprovalOutcome.Allowed, result.Outcome);
        Assert.StartsWith("git version", await File.ReadAllTextAsync(Path.Combine(harness.ManagedTemporaryDirectory, "version.txt"), Ct));
        Assert.False(File.Exists(Path.Combine(project, "version.txt")));
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(
        string root, string project, string id, ToolApprovalMode fileMode,
        ToolApprovalMode shellMode)
        => ShellApprovalHarness.CreateAsync(id,
            new ShellApprovalInvocation("uv export", ApprovalDirectoryShape.Project, Host: ShellApprovalHost.Bash52),
            Approvals.PersistentRepository("uv export"), fixture.ActorSystem, Ct,
            scope: new ShellApprovalHarnessScope(project,
                Directory.CreateDirectory(Path.Combine(root, id)).FullName, $"signalr/{id}", [])
                { RepositoryGrantWorktree = project },
            shellApprovalMode: shellMode,
            policy: new ShellApprovalHarnessPolicy
            {
                PersonalApprovalOverrides = new Dictionary<string, ToolApprovalMode> { [FileWriteTool.ToolName] = fileMode }
            });

    private static string InitializeRepository(string root)
    {
        var project = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("init");
        start.ArgumentList.Add(project);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return project;
    }
}
