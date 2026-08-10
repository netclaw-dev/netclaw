// -----------------------------------------------------------------------
// <copyright file="PowerShellHostProbeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Netclaw.Daemon.Tests;

public class PowerShellHostProbeTests
{
    private const string ExecutablePath = "C:\\PowerShell\\7\\pwsh.exe";

    [Fact]
    public void Probe_process_uses_exact_non_interactive_arguments()
    {
        var startInfo = PowerShellProbeProcessFactory.CreateStartInfo(ExecutablePath);

        Assert.Equal(ExecutablePath, startInfo.FileName);
        Assert.Equal(
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                PowerShellProbeProcessFactory.VersionProbeSource
            ],
            startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void Windows_PATH_locator_uses_first_fully_qualified_match()
    {
        var inspected = new List<string>();
        var locator = new WindowsPathPowerShellExecutableLocator(
            () => "relative;\"C:\\First Tools\";C:\\Second",
            path =>
            {
                inspected.Add(path);
                return path.StartsWith("C:\\First", StringComparison.Ordinal)
                    ? ExecutablePathInspection.File
                    : ExecutablePathInspection.Missing;
            },
            path => path);

        var result = locator.Locate("pwsh.exe");

        var found = Assert.IsType<PowerShellExecutableLookup.Found>(result);
        Assert.Equal("C:\\First Tools\\pwsh.exe", found.ExecutablePath);
        Assert.Equal(["C:\\First Tools\\pwsh.exe"], inspected);
    }

    [Fact]
    public void Windows_PATH_locator_keeps_access_failure_distinct_from_missing()
    {
        var locator = new WindowsPathPowerShellExecutableLocator(
            () => "C:\\Restricted;C:\\Later",
            _ => ExecutablePathInspection.AccessDenied,
            path => path);

        var result = locator.Locate("pwsh.exe");

        var failed = Assert.IsType<PowerShellExecutableLookup.Failed>(result);
        Assert.Equal(PowerShellProbeFailure.AccessDenied, failed.Failure);
    }

    [Fact]
    public async Task Valid_single_version_returns_absolute_host_identity()
    {
        var process = new ControlledProbeProcess("7.6.4", string.Empty);
        var probe = CreateProbe(process);

        var result = await probe.ProbeAsync(
            "pwsh.exe",
            TestContext.Current.CancellationToken);

        var found = Assert.IsType<PowerShellHostProbeResult.Found>(result);
        Assert.Equal(ExecutablePath, found.ExecutablePath);
        Assert.Equal(new Version(7, 6, 4), found.Version);
        Assert.True(process.Disposed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("7.6.4\n5.1")]
    public async Task Malformed_version_output_fails_closed(string output)
    {
        var probe = CreateProbe(new ControlledProbeProcess(output, string.Empty));

        var result = await probe.ProbeAsync(
            "pwsh.exe",
            TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PowerShellHostProbeResult.Failed>(result);
        Assert.Equal(PowerShellProbeFailure.MalformedVersion, failed.Failure);
    }

    [Fact]
    public async Task Error_output_fails_closed_even_with_zero_exit()
    {
        var probe = CreateProbe(new ControlledProbeProcess("7.6.4", "unexpected warning"));

        var result = await probe.ProbeAsync(
            "pwsh.exe",
            TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PowerShellHostProbeResult.Failed>(result);
        Assert.Equal(PowerShellProbeFailure.UnexpectedErrorOutput, failed.Failure);
    }

    [Fact]
    public async Task Nonzero_exit_fails_closed()
    {
        var probe = CreateProbe(new ControlledProbeProcess("7.6.4", string.Empty, exitCode: 9));

        var result = await probe.ProbeAsync(
            "pwsh.exe",
            TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PowerShellHostProbeResult.Failed>(result);
        Assert.Equal(PowerShellProbeFailure.NonZeroExit, failed.Failure);
        Assert.Equal(9, failed.ExitCode);
    }

    [Fact]
    public async Task Oversized_output_fails_closed_after_bounded_capture()
    {
        var probe = CreateProbe(new ControlledProbeProcess(new string('7', 4097), string.Empty));

        var result = await probe.ProbeAsync(
            "pwsh.exe",
            TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PowerShellHostProbeResult.Failed>(result);
        Assert.Equal(PowerShellProbeFailure.OutputTooLarge, failed.Failure);
    }

    [Fact]
    public async Task Timeout_terminates_process_tree_without_waiting_real_time()
    {
        var timeProvider = new FakeTimeProvider();
        var process = new ControlledProbeProcess(
            "7.6.4",
            string.Empty,
            waitForKill: true);
        var probe = CreateProbe(process, timeProvider);

        var pending = probe.ProbeAsync("pwsh.exe", TestContext.Current.CancellationToken);
        await process.WaitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(PowerShellHostProbe.ProbeTimeout);
        var result = await pending;

        var failed = Assert.IsType<PowerShellHostProbeResult.Failed>(result);
        Assert.Equal(PowerShellProbeFailure.Timeout, failed.Failure);
        Assert.True(process.KillTreeCalled);
        Assert.True(process.WaitedAfterKill);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Post_start_failure_terminates_process_and_observes_both_readers()
    {
        await AssertPostStartFailureCleanupAsync(
            new IOException("Synthetic post-start wait failure."),
            cleanupWaitException: null,
            PowerShellProbeFailure.StartFailed);
    }

    [Theory]
    [InlineData(false, (int)PowerShellProbeFailure.StartFailed)]
    [InlineData(true, (int)PowerShellProbeFailure.TerminationFailed)]
    public async Task Win32_wait_failure_cannot_bypass_cleanup(
        bool cleanupWaitFails,
        int expectedFailureValue)
    {
        await AssertPostStartFailureCleanupAsync(
            new Win32Exception(5, "Synthetic initial wait failure."),
            cleanupWaitFails
                ? new Win32Exception(6, "Synthetic cleanup wait failure.")
                : null,
            (PowerShellProbeFailure)expectedFailureValue);
    }

    [Fact]
    public async Task Termination_wait_is_bounded_by_fake_time()
    {
        var timeProvider = new FakeTimeProvider();
        var process = new ControlledProbeProcess(
            "7.6.4",
            string.Empty,
            waitForKill: true,
            completeAfterKill: false);
        var probe = CreateProbe(process, timeProvider);

        var pending = probe.ProbeAsync("pwsh.exe", TestContext.Current.CancellationToken);
        await process.WaitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(PowerShellHostProbe.ProbeTimeout);
        await process.CleanupWaitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(pending.IsCompleted);

        timeProvider.Advance(PowerShellHostProbe.TerminationTimeout);
        var result = await pending;

        var failed = Assert.IsType<PowerShellHostProbeResult.Failed>(result);
        Assert.Equal(PowerShellProbeFailure.TerminationFailed, failed.Failure);
        Assert.True(process.KillTreeCalled);
        Assert.True(process.Disposed);
    }

    private static PowerShellHostProbe CreateProbe(
        IPowerShellProbeProcess process,
        TimeProvider? timeProvider = null) =>
        new(
            timeProvider ?? TimeProvider.System,
            new FixedExecutableLocator(),
            new FixedProcessFactory(process));

    private static async Task AssertPostStartFailureCleanupAsync(
        Exception initialWaitException,
        Exception? cleanupWaitException,
        PowerShellProbeFailure expectedFailure)
    {
        var process = new FaultingLiveProbeProcess(
            initialWaitException,
            cleanupWaitException);
        var probe = CreateProbe(process);

        var pending = probe.ProbeAsync("pwsh.exe", TestContext.Current.CancellationToken);
        await process.StandardOutputReader.CancellationObserved.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        await process.StandardErrorReader.CancellationObserved.Task.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.True(process.KillTreeCalled);
        Assert.True(process.WaitedAfterKill);
        Assert.False(pending.IsCompleted);

        process.StandardOutputReader.Complete();
        process.StandardErrorReader.Complete();
        var result = await pending;

        var failed = Assert.IsType<PowerShellHostProbeResult.Failed>(result);
        Assert.Equal(expectedFailure, failed.Failure);
        Assert.True(process.Disposed);
    }

    private sealed class FixedExecutableLocator : IPowerShellExecutableLocator
    {
        public PowerShellExecutableLookup Locate(string executableName) =>
            new PowerShellExecutableLookup.Found(ExecutablePath);
    }

    private sealed class FixedProcessFactory(IPowerShellProbeProcess process)
        : IPowerShellProbeProcessFactory
    {
        public IPowerShellProbeProcess Start(string executablePath)
        {
            Assert.Equal(ExecutablePath, executablePath);
            return process;
        }
    }

    private sealed class ControlledProbeProcess : IPowerShellProbeProcess
    {
        private readonly TaskCompletionSource _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _waitForKill;
        private readonly bool _completeAfterKill;
        private int _waitCount;

        public ControlledProbeProcess(
            string standardOutput,
            string standardError,
            int exitCode = 0,
            bool waitForKill = false,
            bool completeAfterKill = true)
        {
            StandardOutput = new StringReader(standardOutput);
            StandardError = new StringReader(standardError);
            ExitCode = exitCode;
            _waitForKill = waitForKill;
            _completeAfterKill = completeAfterKill;
            if (!waitForKill)
                _exit.TrySetResult();
        }

        public TaskCompletionSource WaitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CleanupWaitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TextReader StandardOutput { get; }

        public TextReader StandardError { get; }

        public int ExitCode { get; }

        public bool KillTreeCalled { get; private set; }

        public bool WaitedAfterKill { get; private set; }

        public bool Disposed { get; private set; }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            var waitCount = Interlocked.Increment(ref _waitCount);
            if (waitCount == 1)
                WaitStarted.TrySetResult();
            else
                CleanupWaitStarted.TrySetResult();
            if (KillTreeCalled)
                WaitedAfterKill = true;
            await _exit.Task.WaitAsync(cancellationToken);
        }

        public bool TryKillTree()
        {
            KillTreeCalled = true;
            if (_waitForKill && _completeAfterKill)
                _exit.TrySetResult();
            return true;
        }

        public void Dispose()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
            Disposed = true;
        }
    }

    private sealed class FaultingLiveProbeProcess(
        Exception initialWaitException,
        Exception? cleanupWaitException) : IPowerShellProbeProcess
    {
        public BlockingTrackingTextReader StandardOutputReader { get; } = new();

        public BlockingTrackingTextReader StandardErrorReader { get; } = new();

        public TextReader StandardOutput => StandardOutputReader;

        public TextReader StandardError => StandardErrorReader;

        public int ExitCode => 0;

        public bool KillTreeCalled { get; private set; }

        public bool WaitedAfterKill { get; private set; }

        public bool Disposed { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!KillTreeCalled)
                throw initialWaitException;

            WaitedAfterKill = true;
            if (cleanupWaitException is not null)
                throw cleanupWaitException;
            return Task.CompletedTask;
        }

        public bool TryKillTree()
        {
            KillTreeCalled = true;
            return true;
        }

        public void Dispose()
        {
            StandardOutputReader.Dispose();
            StandardErrorReader.Dispose();
            Disposed = true;
        }
    }

    internal sealed class BlockingTrackingTextReader : TextReader
    {
        private readonly TaskCompletionSource<int> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _cancellationRegistration;

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            _cancellationRegistration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                CancellationObserved);
            return new ValueTask<int>(_completion.Task);
        }

        public void Complete()
        {
            _completion.TrySetResult(0);
            _cancellationRegistration.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _cancellationRegistration.Dispose();
            base.Dispose(disposing);
        }
    }
}
