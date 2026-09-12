// -----------------------------------------------------------------------
// <copyright file="DispatchingToolExecutorLaunchTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public partial class DispatchingToolExecutorTests
{
    [Fact]
    public async Task Background_launch_rejects_missing_trust_context_before_the_handoff()
    {
        using var directory = new DisposableTempDir();
        var context = TestToolExecutionContext.CreateBound("launch/missing-boundary", directory.Path, TrustAudience.Personal);
        var call = CreateToolCall("missing-boundary", ShellTool.ToolName, ToolInput.Create("Command", "echo rejected"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _executor.PrepareShellLaunchAsync(call, context, TestContext.Current.CancellationToken));

        Assert.Contains("trust boundary", failure.Message);
    }

    [Fact]
    public async Task Background_launch_rejects_a_cwd_that_depends_on_the_daemon_directory()
    {
        using var directory = new DisposableTempDir();
        var context = TestToolExecutionContext.CreateBound("launch/relative", directory.Path,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal });
        var call = CreateToolCall("relative-cwd", ShellTool.ToolName,
            ToolInput.Create("Command", "echo rejected", "WorkingDirectory", "."));

        var failure = await Assert.ThrowsAsync<ShellProcessStartException>(() =>
            _executor.PrepareShellLaunchAsync(call, context, TestContext.Current.CancellationToken));

        Assert.Contains("absolute working directory", failure.Message);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("stream")]
    [InlineData("background")]
    public async Task Launch_rechecks_revoked_grants_in_every_mode(string mode)
    {
        using var directory = new DisposableTempDir();
        // macOS temporary roots contain symlinks. Stored-grant tests need a physical path, not an exact-approval path.
        ToolPathPolicy.TryResolveSymlinksInPath(directory.Path, out var sessionDirectory);
        var (registry, policy) = CreateApprovalGatedShellRegistryAndPolicy(ShellEnvironment);
        var checks = 0;
        var service = new FixedShellApprovalService(request =>
            LaunchGrantResult(request, ++checks == 1));
        var executor = new DispatchingToolExecutor(registry, policy, service);
        var context = TestToolExecutionContext.CreateBound("launch/revoked", sessionDirectory,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal });
        var marker = Path.Combine(directory.Path, "must-not-exist.txt");
        var call = CreateToolCall("launch-revoked", ShellTool.ToolName,
            ToolInput.Create("Command", "echo forbidden > must-not-exist.txt"));

        await Assert.ThrowsAsync<ToolApprovalRequiredException>(async () =>
        {
            switch (mode)
            {
                case "normal":
                    await executor.ExecuteAsync(call, context, TestContext.Current.CancellationToken);
                    break;
                case "stream":
                    await foreach (var unused in executor.ExecuteStreamAsync(call, context, TestContext.Current.CancellationToken))
                    {
                        Assert.Fail("A revoked launch must not emit a process result.");
                    }
                    break;
                case "background":
                    var launch = await executor.PrepareShellLaunchAsync(call, context, TestContext.Current.CancellationToken);
                    (await launch.StartAsync(TestContext.Current.CancellationToken)).Dispose();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        });

        Assert.Equal(2, checks);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Launch_retains_exact_arguments_and_starts_once()
    {
        using var directory = new DisposableTempDir();
        ToolPathPolicy.TryResolveSymlinksInPath(directory.Path, out var sessionDirectory);
        var (registry, policy) = CreateApprovalGatedShellRegistryAndPolicy(ShellEnvironment);
        var service = new FixedShellApprovalService(request => LaunchGrantResult(request, true));
        var executor = new DispatchingToolExecutor(registry, policy, service);
        var context = TestToolExecutionContext.CreateBound("launch/exact", sessionDirectory,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal });
        var call = CreateToolCall("launch-exact", ShellTool.ToolName,
            ToolInput.Create("Command", "echo once >> count.txt"));
        var launch = await executor.PrepareShellLaunchAsync(call, context, TestContext.Current.CancellationToken);

        call.Arguments!["Command"] = "echo changed > changed.txt";
        call.Arguments["WorkingDirectory"] = Path.GetTempPath();
        using var process = await launch.StartAsync(TestContext.Current.CancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(directory.Path, "count.txt"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(directory.Path, "changed.txt")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => launch.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, service.RequestCount);
    }

    [Fact]
    public async Task Launch_cancellation_after_authorization_creates_no_process()
    {
        using var directory = new DisposableTempDir();
        ToolPathPolicy.TryResolveSymlinksInPath(directory.Path, out var sessionDirectory);
        var (registry, policy) = CreateApprovalGatedShellRegistryAndPolicy(ShellEnvironment);
        var executor = new DispatchingToolExecutor(registry, policy, GrantEveryShellCandidate());
        var context = TestToolExecutionContext.CreateBound("launch/cancel", sessionDirectory,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal });
        var call = CreateToolCall("launch-cancel", ShellTool.ToolName,
            ToolInput.Create("Command", "echo forbidden > cancelled.txt"));
        var launch = await executor.PrepareShellLaunchAsync(call, context, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => launch.StartAsync(cancellation.Token));
        Assert.False(File.Exists(Path.Combine(directory.Path, "cancelled.txt")));
    }

    [Fact]
    public async Task Launch_keeps_exact_one_time_approval_after_the_submitter_clears_its_state()
    {
        using var directory = new DisposableTempDir();
        var (registry, policy) = CreateApprovalGatedShellRegistryAndPolicy(ShellEnvironment);
        var service = new FixedShellApprovalService(request => LaunchGrantResult(request, false));
        var executor = new DispatchingToolExecutor(registry, policy, service);
        var context = TestToolExecutionContext.CreateBound("launch/one-time", directory.Path,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal });
        var call = CreateToolCall("launch-one-time", ShellTool.ToolName,
            ToolInput.Create("Command", "echo approved > once.txt"));
        var decision = await executor.EvaluateAuthorizationAsync(call, context, TestContext.Current.CancellationToken);
        var approval = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        context.Approval.SeedOneTimeApproval(ShellTool.ToolName, OneTimeApprovalKeys.Create(approval));
        var launch = await executor.PrepareShellLaunchAsync(call, context, TestContext.Current.CancellationToken);
        context.Approval.ClearOneTimeApproval();

        using var process = await launch.StartAsync(TestContext.Current.CancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("approved", (await File.ReadAllTextAsync(Path.Combine(directory.Path, "once.txt"), TestContext.Current.CancellationToken)).Trim());
        Assert.Null(context.Approval.OneTimeApprovedToolName);
    }

    public static bool SupportsLaunchLinks => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic-link behavior.")]
    [Fact(SkipUnless = nameof(SupportsLaunchLinks), Skip = "POSIX-only symbolic-link semantics")]
    public async Task Launch_rejects_causal_path_changes_inside_the_grant_await()
    {
        using var directory = new DisposableTempDir();
        var alias = Directory.CreateDirectory(Path.Combine(directory.Path, "alias")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(directory.Path, "outside")).FullName;
        var (registry, policy) = CreateApprovalGatedShellRegistryAndPolicy(ShellEnvironment,
            safeVerbs: SafeVerbList.FromVerbs(ApprovalShell.Bash, ["head"]));
        var calls = 0;
        var service = new FixedShellApprovalService(request =>
        {
            if (++calls == 2)
            {
                Directory.Delete(alias);
                Directory.CreateSymbolicLink(alias, outside);
            }
            return LaunchGrantResult(request, true);
        });
        var executor = new DispatchingToolExecutor(registry, policy, service);
        var context = TestToolExecutionContext.CreateBound("launch/causal", directory.Path,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal });
        var call = CreateToolCall("launch-causal", ShellTool.ToolName,
            ToolInput.Create("Command", $"cd '{alias}' && echo forbidden > marker.txt; head result.log"));
        var launch = await executor.PrepareShellLaunchAsync(call, context, TestContext.Current.CancellationToken);

        var denied = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => launch.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("shell_launch_paths_changed", denied.DenyReason);
        Assert.Equal(2, calls);
        Assert.False(File.Exists(Path.Combine(outside, "marker.txt")));
    }

    private static ShellApprovalMatchResult LaunchGrantResult(ShellApprovalMatchRequest request, bool approved)
        => new(new PersistentGrantStoreStatus.Ready(),
            request.Candidates.Select(candidate => approved
                ? new ShellGrantCandidateMatch(candidate.CandidateId,
                    new ToolApprovalMatch(candidate.Candidate.Verb, "session", "this chat"),
                    ShellCoverageKind.Session, [])
                : new ShellGrantCandidateMatch(candidate.CandidateId, null, null, [])).ToArray());
}
