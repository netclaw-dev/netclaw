// -----------------------------------------------------------------------
// <copyright file="ManagedScratchApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security.Authorization.Filesystem;
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
        var externalOption = await harness.EvaluateShellAsync($"uv export --output='{external}' > \"$TMPDIR/reqs.txt\"", Ct, project);
        Assert.NotEqual(ApprovalOutcome.Allowed, externalOption.Outcome);
        var qualified = await harness.EvaluateShellAsync("NC_APPROVAL_SCOPE=one uv export > \"$TMPDIR/reqs.txt\"", Ct, project);
        Assert.NotEqual(ApprovalOutcome.Allowed, qualified.Outcome);
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
    [Theory(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash paths.")]
    [InlineData(ToolApprovalMode.Approval)]
    [InlineData(ToolApprovalMode.Auto)]
    public async Task Scratch_once_answer_binds_the_command_and_file_operations(ToolApprovalMode shellMode)
    {
        using var root = new DisposableTempDir();
        var project = InitializeRepository(root.Path);
        await using var harness = await CreateHarnessAsync(
            root.Path, project, "once", ToolApprovalMode.Approval, shellMode);
        harness.AddStoredShellEntry(TrustAudience.Personal,
            ApprovalEntry.CreateRepositoryTokenPrefix(ApprovalShell.Bash, ["uv", "sync"], Path.Combine(project, ".git")));
        Directory.CreateDirectory(harness.ManagedTemporaryDirectory);

        const string command = "uv export > \"$TMPDIR/reqs.txt\"";
        var prompt = await harness.EvaluateShellAsync(command, Ct, project);
        Assert.Equal(ApprovalOutcome.RequiresApproval, prompt.Outcome);
        harness.SeedOneTimeApproval(prompt.Prompt!);

        foreach (var changed in new[]
        {
            "uv sync > \"$TMPDIR/reqs.txt\"",
            "uv export > \"$TMPDIR/other.txt\"",
            "NC_APPROVAL_SCOPE=one uv export > \"$TMPDIR/reqs.txt\""
        })
        {
            var refused = await harness.EvaluateShellAsync(changed, Ct, project);
            Assert.Equal(ApprovalOutcome.RequiresApproval, refused.Outcome);
        }

        var retry = await harness.EvaluateShellAsync(command, Ct, project);
        Assert.Equal(ApprovalOutcome.Allowed, retry.Outcome);
        Assert.Equal(ApprovalAllowReason.OneTimeApproval, retry.AllowReason);
    }

    [SlopwatchSuppress("SW001", "Scratch composition currently supports Personal Bash calls on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash paths.")]
    [InlineData("uv export")]
    [InlineData(":")]
    public async Task Once_answer_cannot_split_one_filename_into_two_targets(string program)
    {
        using var root = new DisposableTempDir();
        var project = InitializeRepository(root.Path);
        await using var harness = await CreateHarnessAsync(
            root.Path, project, "file-list", ToolApprovalMode.Approval, ToolApprovalMode.Approval);
        var managed = harness.ManagedTemporaryDirectory;
        var collision = $"{managed}/first, write {managed}/second";
        Directory.CreateDirectory(Path.GetDirectoryName(collision)!);
        var command = $"{program} > \"{collision}\"";
        var prompt = await harness.EvaluateShellAsync(command, Ct, project);
        Assert.Equal(ApprovalOutcome.RequiresApproval, prompt.Outcome);
        harness.SeedOneTimeApproval(prompt.Prompt!);

        var split = await harness.EvaluateShellAsync($"{program} > \"$TMPDIR/first\" 2> \"$TMPDIR/second\"", Ct, project);
        Assert.Equal(ApprovalOutcome.RequiresApproval, split.Outcome);
        var retry = await harness.EvaluateShellAsync(command, Ct, project);
        Assert.Equal(ApprovalOutcome.Allowed, retry.Outcome);
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
        Assert.Contains("does not cover subcommands", bare.ApprovalContext!.Notes, StringComparison.Ordinal);
        Assert.Equal("uv --version", bare.ApprovalContext!.DisplayText);
        var mismatch = await harness.EvaluateShellDecisionAsync("uv run pytest", Ct);
        Assert.Contains("different command words", mismatch.ApprovalContext!.Notes, StringComparison.Ordinal);
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

    /// <summary>
    /// PR #2559 review item S6. Issue #2554 moves <c>temp_dir</c> on POSIX to the short layout
    /// <c>&lt;base&gt;/&lt;key&gt;/p</c>, and the base is below the platform temporary root. The
    /// #2563 route (a repository grant composed with managed scratch authority) must work
    /// there too: the own scratch redirect needs no consent and gets no temp-path advice,
    /// before and after the first launch makes the private folders. The scratch folder of
    /// another session stays outside the composition.
    /// </summary>
    [SlopwatchSuppress("SW001", "The short private layout and scratch composition exist only on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash paths.")]
    public async Task Repository_grant_composes_with_owned_scratch_in_the_short_private_layout()
    {
        using var root = new DisposableTempDir();
        var project = InitializeRepository(root.Path);
        FileSystemAuthority.TryResolveLinks(root.Path, out var physicalRoot);
        var temporaryBase = Path.Combine(physicalRoot, "netclaw-base");
        var layout = new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(temporaryBase));
        await using var harness = await CreateHarnessAsync(
            root.Path, project, "short", ToolApprovalMode.Auto, ToolApprovalMode.Approval, layout);
        await using var other = await CreateHarnessAsync(
            root.Path, project, "short-other", ToolApprovalMode.Auto, ToolApprovalMode.Approval, layout);
        Assert.StartsWith(temporaryBase + "/", harness.ManagedTemporaryDirectory);
        FileSystemAuthority.TryResolveLinks(Path.GetTempPath(), out var platformTemporaryRoot);
        Assert.StartsWith(platformTemporaryRoot.TrimEnd('/') + "/", harness.ManagedTemporaryDirectory);

        // The first call of a session: no launch made the private folders yet.
        var first = await harness.EvaluateShellAsync("uv export --frozen > \"$TMPDIR/reqs.txt\"", Ct, project);
        Assert.True(first.Outcome == ApprovalOutcome.Allowed, System.Text.Json.JsonSerializer.Serialize(first));
        Assert.Null(first.AgentCorrectionTarget);
        Assert.Contains(first.ApprovalMatches, match => match.Contains("uv export", StringComparison.Ordinal));

        harness.AddStoredShellEntry(TrustAudience.Personal,
            ApprovalEntry.CreateRepositoryTokenPrefix(ApprovalShell.Bash, ["git"], Path.Combine(project, ".git")));
        var launch = await harness.RunToolAsync(ShellTool.ToolName, new Dictionary<string, object?>
        {
            ["Command"] = "git --version > \"$TMPDIR/version.txt\"",
            ["WorkingDirectory"] = project
        }, Ct);
        Assert.True(launch.Outcome == ApprovalOutcome.Allowed, System.Text.Json.JsonSerializer.Serialize(launch));
        Assert.StartsWith("git version", await File.ReadAllTextAsync(
            Path.Combine(harness.ManagedTemporaryDirectory, "version.txt"), Ct));
        Assert.False(File.Exists(Path.Combine(project, "version.txt")));

        var again = await harness.EvaluateShellAsync("uv export --frozen > \"$TMPDIR/reqs.txt\"", Ct, project);
        Assert.True(again.Outcome == ApprovalOutcome.Allowed, System.Text.Json.JsonSerializer.Serialize(again));
        Assert.Null(again.AgentCorrectionTarget);

        var foreign = await harness.EvaluateShellAsync(
            $"uv export > '{other.ManagedTemporaryDirectory}/reqs.txt'", Ct, project);
        Assert.NotEqual(ApprovalOutcome.Allowed, foreign.Outcome);
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(
        string root, string project, string id, ToolApprovalMode fileMode,
        ToolApprovalMode shellMode)
        => CreateHarnessAsync(root, project, id, fileMode, shellMode, temporaryLayout: null);

    private Task<ShellApprovalHarness> CreateHarnessAsync(
        string root, string project, string id, ToolApprovalMode fileMode,
        ToolApprovalMode shellMode, ManagedTemporaryLayout? temporaryLayout)
    {
        FileSystemAuthority.TryResolveLinks(root, out var physicalRoot);
        return ShellApprovalHarness.CreateAsync(id,
            new ShellApprovalInvocation("uv export", ApprovalDirectoryShape.Project, Host: ShellApprovalHost.Bash52),
            Approvals.PersistentRepository("uv export"), fixture.ActorSystem, Ct,
            scope: new ShellApprovalHarnessScope(project,
                Directory.CreateDirectory(Path.Combine(physicalRoot, id)).FullName, $"signalr/{id}", [])
                { RepositoryGrantWorktree = project, TemporaryLayout = temporaryLayout },
            shellApprovalMode: shellMode,
            policy: new ShellApprovalHarnessPolicy
            {
                PersonalApprovalOverrides = new Dictionary<string, ToolApprovalMode> { [FileWriteTool.ToolName] = fileMode }
            });
    }

    private static string InitializeRepository(string root)
    {
        var project = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        FileSystemAuthority.TryResolveLinks(project, out project);
        var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("init");
        start.ArgumentList.Add(project);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return project;
    }
}
