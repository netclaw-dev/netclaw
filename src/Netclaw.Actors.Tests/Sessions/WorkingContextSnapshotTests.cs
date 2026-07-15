// -----------------------------------------------------------------------
// <copyright file="WorkingContextSnapshotTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;

namespace Netclaw.Actors.Tests.Sessions;

public class WorkingContextSnapshotTests
{
    [Fact]
    public void ParseStatus_reads_branch_divergence_and_dirty_counts()
    {
        var snapshot = GitWorkingContextInspector.ParseStatus(
            "/worktrees/feature",
            "/repos/app/.git",
            """
            # branch.oid 0123456789abcdef
            # branch.head feature/context
            # branch.upstream origin/dev
            # branch.ab +2 -1
            1 M. N... 100644 100644 100644 aaaaaaa bbbbbbb src/Staged.cs
            1 .M N... 100644 100644 100644 aaaaaaa bbbbbbb src/Modified.cs
            ? src/New.cs
            """);

        Assert.Equal("feature/context", snapshot.Branch);
        Assert.Equal("0123456789abcdef", snapshot.Head);
        Assert.Equal("origin/dev", snapshot.Upstream);
        Assert.Equal(2, snapshot.Ahead);
        Assert.Equal(1, snapshot.Behind);
        Assert.Equal(1, snapshot.Staged);
        Assert.Equal(1, snapshot.Modified);
        Assert.Equal(1, snapshot.Untracked);
        Assert.Equal(3, snapshot.ChangedFiles.Count);
    }

    [Fact]
    public void ParseStatus_uses_rename_destination_as_changed_file()
    {
        var snapshot = GitWorkingContextInspector.ParseStatus(
            "/worktrees/feature",
            "/repos/app/.git",
            "2 R. N... 100644 100644 100644 aaaaaaa bbbbbbb R100 src/New Name.cs\tsrc/Old Name.cs");

        Assert.Equal(["src/New Name.cs"], snapshot.ChangedFiles);
    }

    [Fact]
    public void Render_nests_git_under_working_context_without_remote_url()
    {
        var snapshot = new WorkingContextSnapshot
        {
            WorkingContext = WorkingContext.Empty
                .WithProjectDirectory("/worktrees/feature")
                .AddRecentFile("src/App.cs"),
            Git = new GitWorkingContextInspection.Available(new GitWorkingContextSnapshot
            {
                Worktree = "/worktrees/feature",
                CommonDirectory = "/repos/app/.git",
                Branch = "feature/context",
                Head = "01234567",
                Upstream = "origin/dev",
                Staged = 1,
                Modified = 2,
                Untracked = 3
            })
        };

        var block = snapshot.ToContextBlock();

        Assert.Contains("[working-context]", block);
        Assert.Contains("recent_files:\n  - src/App.cs", block);
        Assert.Contains("git:\n  worktree: /worktrees/feature", block);
        Assert.Contains("branch: feature/context", block);
        Assert.DoesNotContain("https://", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Public_audience_does_not_inspect_or_render_git()
    {
        var inspector = new RecordingGitInspector();
        var provider = new WorkingContextSnapshotProvider(
            inspector,
            NullLogger<WorkingContextSnapshotProvider>.Instance);
        var context = WorkingContext.Empty.WithProjectDirectory("/path/that/does/not/exist");

        var snapshot = await provider.CreateAsync(
            context,
            TrustAudience.Public,
            TestContext.Current.CancellationToken);

        Assert.IsType<GitWorkingContextInspection.Skipped>(snapshot.Git);
        Assert.Equal(0, inspector.InvocationCount);
        Assert.Equal(string.Empty, snapshot.ToContextBlock());
    }

    [Fact]
    public async Task Missing_project_directory_reports_unavailable_for_personal_audience()
    {
        var inspector = new RecordingGitInspector();
        var provider = new WorkingContextSnapshotProvider(
            inspector,
            NullLogger<WorkingContextSnapshotProvider>.Instance);
        var context = WorkingContext.Empty.WithProjectDirectory("/path/that/does/not/exist");

        var snapshot = await provider.CreateAsync(
            context,
            TrustAudience.Personal,
            TestContext.Current.CancellationToken);

        var unavailable = Assert.IsType<GitWorkingContextInspection.Unavailable>(snapshot.Git);
        Assert.Equal("project directory does not exist", unavailable.Reason);
        Assert.Equal(0, inspector.InvocationCount);
        Assert.Contains("status: unavailable", snapshot.ToContextBlock());
    }

    [Fact]
    public async Task Git_inspection_shares_one_deadline_across_root_and_status_commands()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-14T12:00:00Z"));
        var runner = new SequenceGitCommandRunner(time,
        [
            new TimedGitResult(TimeSpan.FromMilliseconds(1_500), GitCommandResult.Succeeded(
                "/repo\n/repo/.git\n",
                string.Empty)),
            new TimedGitResult(TimeSpan.Zero, GitCommandResult.Succeeded(
                "# branch.oid 01234567\n# branch.head dev\n",
                string.Empty))
        ]);
        var inspector = new GitWorkingContextInspector(runner, time);

        var result = await inspector.InspectAsync(
            "/repo",
            TestContext.Current.CancellationToken);

        Assert.IsType<GitWorkingContextInspection.Available>(result);
        Assert.Equal(2, runner.Timeouts.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), runner.Timeouts[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(500), runner.Timeouts[1]);
    }

    [Fact]
    public async Task Git_inspection_does_not_launch_status_after_aggregate_deadline_expires()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-14T12:00:00Z"));
        var runner = new SequenceGitCommandRunner(time,
        [
            new TimedGitResult(TimeSpan.FromSeconds(2), GitCommandResult.Succeeded(
                "/repo\n/repo/.git\n",
                string.Empty))
        ]);
        var inspector = new GitWorkingContextInspector(runner, time);

        var result = await inspector.InspectAsync(
            "/repo",
            TestContext.Current.CancellationToken);

        var unavailable = Assert.IsType<GitWorkingContextInspection.Unavailable>(result);
        Assert.Equal("git inspection timed out", unavailable.Reason);
        Assert.Single(runner.Timeouts);
    }

    [Fact]
    public async Task Git_inspection_classifies_non_repository_without_status_command()
    {
        var time = new FakeTimeProvider();
        var runner = new SequenceGitCommandRunner(time,
        [
            new TimedGitResult(TimeSpan.Zero, GitCommandResult.Failed(
                "fatal: not a git repository",
                stdout: string.Empty,
                stderr: "fatal: not a git repository"))
        ]);
        var inspector = new GitWorkingContextInspector(runner, time);

        var result = await inspector.InspectAsync(
            "/repo",
            TestContext.Current.CancellationToken);

        Assert.IsType<GitWorkingContextInspection.NotRepository>(result);
        Assert.Single(runner.Timeouts);
    }

    [Fact]
    public async Task Git_inspection_reports_missing_git_without_status_command()
    {
        var time = new FakeTimeProvider();
        var runner = new SequenceGitCommandRunner(time,
        [
            new TimedGitResult(TimeSpan.Zero, GitCommandResult.ExecutableNotFound())
        ]);
        var inspector = new GitWorkingContextInspector(runner, time);

        var result = await inspector.InspectAsync(
            "/repo",
            TestContext.Current.CancellationToken);

        Assert.IsType<GitWorkingContextInspection.ExecutableNotFound>(result);
        Assert.Single(runner.Timeouts);
    }

    [Fact]
    public async Task Git_inspection_reports_malformed_root_response_without_status_command()
    {
        var time = new FakeTimeProvider();
        var runner = new SequenceGitCommandRunner(time,
        [
            new TimedGitResult(TimeSpan.Zero, GitCommandResult.Succeeded("/repo\n", string.Empty))
        ]);
        var inspector = new GitWorkingContextInspector(runner, time);

        var result = await inspector.InspectAsync(
            "/repo",
            TestContext.Current.CancellationToken);

        var unavailable = Assert.IsType<GitWorkingContextInspection.Unavailable>(result);
        Assert.Equal("git returned an unexpected repository-root response", unavailable.Reason);
        Assert.Single(runner.Timeouts);
    }

    [Fact]
    public async Task Git_inspection_reports_malformed_status_response()
    {
        var time = new FakeTimeProvider();
        var runner = new SequenceGitCommandRunner(time,
        [
            new TimedGitResult(TimeSpan.Zero, GitCommandResult.Succeeded(
                "/repo\n/repo/.git\n",
                string.Empty)),
            new TimedGitResult(TimeSpan.Zero, GitCommandResult.Succeeded(
                "# branch.ab malformed\n",
                string.Empty))
        ]);
        var inspector = new GitWorkingContextInspector(runner, time);

        var result = await inspector.InspectAsync(
            "/repo",
            TestContext.Current.CancellationToken);

        var unavailable = Assert.IsType<GitWorkingContextInspection.Unavailable>(result);
        Assert.Equal("git returned an invalid ahead/behind response", unavailable.Reason);
        Assert.Equal(2, runner.Timeouts.Count);
    }

    [Fact]
    public void Git_command_output_is_bounded()
    {
        var oversized = new string('x', 300_000);

        var bounded = GitCommandRunner.Bound(oversized);

        Assert.Equal(256 * 1024, bounded.Length);
    }

    [Fact]
    public async Task Git_command_cancellation_terminates_and_awaits_process()
    {
        var process = new ControlledGitProcess();
        var runner = new GitCommandRunner(new FixedGitProcessFactory(process));
        using var cancellation = new CancellationTokenSource();

        var run = runner.RunAsync(
            "/repo",
            ["status"],
            Timeout.InfiniteTimeSpan,
            cancellation.Token);
        await process.WaitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(process.KillTreeCalled);
        Assert.True(process.WaitedAfterTermination);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Git_command_deadline_terminates_and_awaits_process()
    {
        var process = new ControlledGitProcess();
        var runner = new GitCommandRunner(new FixedGitProcessFactory(process));

        var result = await runner.RunAsync(
            "/repo",
            ["status"],
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("git inspection timed out", result.Error);
        Assert.True(process.KillTreeCalled);
        Assert.True(process.WaitedAfterTermination);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Missing_git_executable_has_stable_typed_result()
    {
        var runner = new GitCommandRunner(new MissingGitProcessFactory());
        var inspector = new GitWorkingContextInspector(runner, TimeProvider.System);

        var result = await inspector.InspectAsync(
            "/repo",
            TestContext.Current.CancellationToken);

        Assert.IsType<GitWorkingContextInspection.ExecutableNotFound>(result);
        var snapshot = new WorkingContextSnapshot
        {
            WorkingContext = WorkingContext.Empty.WithProjectDirectory("/repo"),
            Git = result
        };
        Assert.Contains("reason: git executable not found", snapshot.ToContextBlock());
    }

    [Fact]
    public async Task Non_missing_executable_start_failure_remains_unavailable()
    {
        var runner = new GitCommandRunner(new FailingGitProcessFactory(
            new System.ComponentModel.Win32Exception(5, "access denied")));
        var inspector = new GitWorkingContextInspector(runner, TimeProvider.System);

        var result = await inspector.InspectAsync(
            "/repo",
            TestContext.Current.CancellationToken);

        var unavailable = Assert.IsType<GitWorkingContextInspection.Unavailable>(result);
        Assert.Equal("access denied", unavailable.Reason);
    }

    [Fact]
    public async Task Unavailable_reason_is_single_line_and_bounded_before_rendering()
    {
        var reason = new string('x', 300) + "\ncredential-bearing second line";
        var provider = new WorkingContextSnapshotProvider(
            new FixedGitInspector(new GitWorkingContextInspection.Unavailable(reason)),
            NullLogger<WorkingContextSnapshotProvider>.Instance);
        var context = WorkingContext.Empty.WithProjectDirectory(Path.GetTempPath());

        var snapshot = await provider.CreateAsync(
            context,
            TrustAudience.Team,
            TestContext.Current.CancellationToken);

        var unavailable = Assert.IsType<GitWorkingContextInspection.Unavailable>(snapshot.Git);
        Assert.Equal(200, unavailable.Reason.Length);
        Assert.DoesNotContain("credential-bearing", unavailable.Reason);
    }

    [Theory]
    [InlineData(41, 42, true)]
    [InlineData(42, 42, false)]
    public void Stale_or_cancelled_working_context_continuation_is_rejected(
        long snapshotGeneration,
        long activeGeneration,
        bool hasActiveLlmCall)
    {
        Assert.False(LlmSessionActor.ShouldApplyWorkingContextSnapshot(
            snapshotGeneration,
            activeGeneration,
            hasActiveLlmCall));
    }

    [Fact]
    public void Current_working_context_continuation_is_accepted()
    {
        Assert.True(LlmSessionActor.ShouldApplyWorkingContextSnapshot(
            snapshotGeneration: 42,
            activeGeneration: 42,
            hasActiveLlmCall: true));
    }

    private sealed class RecordingGitInspector : IGitWorkingContextInspector
    {
        public int InvocationCount { get; private set; }

        public Task<GitWorkingContextInspection> InspectAsync(
            string projectDirectory,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromResult<GitWorkingContextInspection>(
                new GitWorkingContextInspection.NotRepository());
        }
    }

    private sealed class FixedGitInspector(GitWorkingContextInspection result)
        : IGitWorkingContextInspector
    {
        public Task<GitWorkingContextInspection> InspectAsync(
            string projectDirectory,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed record TimedGitResult(TimeSpan Elapsed, GitCommandResult Result);

    private sealed class SequenceGitCommandRunner : IGitCommandRunner
    {
        private readonly FakeTimeProvider _timeProvider;
        private readonly Queue<TimedGitResult> _results;

        public SequenceGitCommandRunner(
            FakeTimeProvider timeProvider,
            IReadOnlyList<TimedGitResult> results)
        {
            _timeProvider = timeProvider;
            _results = new Queue<TimedGitResult>(results);
        }

        public List<TimeSpan> Timeouts { get; } = [];

        public Task<GitCommandResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Timeouts.Add(timeout);
            var next = _results.Dequeue();
            _timeProvider.Advance(next.Elapsed);
            return Task.FromResult(next.Result);
        }
    }

    private sealed class FixedGitProcessFactory(IRunningGitProcess process) : IGitProcessFactory
    {
        public IRunningGitProcess Start(string workingDirectory, IReadOnlyList<string> arguments) => process;
    }

    private sealed class MissingGitProcessFactory : IGitProcessFactory
    {
        public IRunningGitProcess Start(string workingDirectory, IReadOnlyList<string> arguments) =>
            throw new System.ComponentModel.Win32Exception(2, "platform-specific message");
    }

    private sealed class FailingGitProcessFactory(Exception failure) : IGitProcessFactory
    {
        public IRunningGitProcess Start(string workingDirectory, IReadOnlyList<string> arguments) => throw failure;
    }

    private sealed class ControlledGitProcess : IRunningGitProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool KillTreeCalled { get; private set; }
        public bool WaitedAfterTermination { get; private set; }
        public bool Disposed { get; private set; }
        public int ExitCode => 0;

        public Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken) =>
            ReadUntilExitAsync(cancellationToken);

        public Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken) =>
            ReadUntilExitAsync(cancellationToken);

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            if (!cancellationToken.CanBeCanceled)
                WaitedAfterTermination = true;
            await _exit.Task.WaitAsync(cancellationToken);
        }

        public bool TryKillTree()
        {
            KillTreeCalled = true;
            _exit.TrySetResult();
            return true;
        }

        public void Dispose() => Disposed = true;

        private async Task<string> ReadUntilExitAsync(CancellationToken cancellationToken)
        {
            await _exit.Task.WaitAsync(cancellationToken);
            return string.Empty;
        }
    }
}
