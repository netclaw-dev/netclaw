// -----------------------------------------------------------------------
// <copyright file="HealthCheckStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class HealthCheckStepViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public HealthCheckStepViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", null);
        _dir.Dispose();
    }

    [Fact]
    public async Task RunWithOrchestrator_PreservesSpecificStartupFailureMessage()
    {
        var fakeBinaryPath = Path.Combine(
            _dir.Path,
            OperatingSystem.IsWindows() ? "fake-netclawd.cmd" : "fake-netclawd.sh");
        await File.WriteAllTextAsync(
            fakeBinaryPath,
            OperatingSystem.IsWindows()
                ? "@echo off\r\nexit /b 1\r\n"
                : "#!/usr/bin/env bash\nexit 1\n",
            TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fakeBinaryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", fakeBinaryPath);

        var expectedMessage =
            "Daemon startup aborted: Invalid reverse-proxy topology: Daemon.Host '127.0.0.1' is loopback.";
        var crashLogPath = Path.Combine(_paths.LogsDirectory, "crash-test.log");
        await File.WriteAllTextAsync(
            crashLogPath,
            $"{expectedMessage}{Environment.NewLine}",
            TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(crashLogPath, DateTime.UtcNow.AddMinutes(1));

        var daemonManager = new DaemonManager(_paths, TimeProvider.System);
        using var step = new HealthCheckStepViewModel(
            daemonManager,
            daemonApi: null,
            navigationState: new ChatNavigationState());
        using var exposureStep = new ExposureModeStepViewModel
        {
            SelectedMode = ExposureMode.ReverseProxy
        };

        using var context = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };

        step.OnEnter(context, NavigationDirection.Forward);
        exposureStep.OnEnter(context, NavigationDirection.Forward);

        using var orchestrator = new WizardOrchestrator([exposureStep, step], context);

        await step.RunWithOrchestrator(orchestrator);

        Assert.NotEmpty(step.Results);
        var failure = Assert.Single(step.Results, result => result.Passed is false);
        Assert.Contains(expectedMessage, failure.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Daemon did not become ready", failure.Label, StringComparison.Ordinal);
        Assert.Contains(crashLogPath, failure.Label, StringComparison.Ordinal);
        Assert.Equal(
            "Setup complete with warnings. Run `netclaw daemon start`, then `netclaw chat`. Adjust settings with `netclaw config`.",
            context.StatusMessage.Value);
        Assert.False(step.Succeeded.Value);
    }

    [Fact]
    public async Task ResultsSnapshot_is_safe_to_read_while_results_are_mutated_concurrently()
    {
        var daemonManager = new DaemonManager(_paths, TimeProvider.System);
        using var step = new HealthCheckStepViewModel(
            daemonManager, daemonApi: null, navigationState: new ChatNavigationState());

        var runner = new HealthCheckRunner(step.Results, () => { });
        using var cts = new CancellationTokenSource();
        var firstResultAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Factory.StartNew(() =>
        {
            var writeCount = 0;
            while (!cts.IsCancellationRequested)
            {
                runner.Add(new HealthCheckItem("probe", true));
                firstResultAdded.TrySetResult();

                if (++writeCount % 128 != 0)
                    continue;

                lock (step.Results)
                {
                    if (step.Results.Count > 256)
                        step.Results.RemoveRange(0, step.Results.Count - 256);
                }

                Thread.Yield();
            }
        },
        TestContext.Current.CancellationToken,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);

        try
        {
            // Wait for the writer to start producing before the read loop, so the reads genuinely
            // race concurrent Adds AND the final non-empty assertion is deterministic. On a contended
            // CI scheduler the writer may not run before cts.Cancel(), which left Results empty and
            // failed the assertion intermittently.
            await firstResultAdded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Read snapshots while the writer mutates Results off-thread. Without the synchronized
            // snapshot, ToArray throws "Collection was modified" during a concurrent Add.
            for (var i = 0; i < 50_000; i++)
                _ = step.ResultsSnapshot();
        }
        finally
        {
            cts.Cancel();
            await writer.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        Assert.NotEmpty(step.ResultsSnapshot());
    }

    [Fact]
    public async Task OnEnter_Forward_AfterFailedRun_ResetsStateForRetry()
    {
        using var step = new HealthCheckStepViewModel(
            daemonManager: null,
            daemonApi: null,
            navigationState: new ChatNavigationState());
        using var exposureStep = new ExposureModeStepViewModel
        {
            SelectedMode = ExposureMode.Local
        };

        using var context = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };

        step.OnEnter(context, NavigationDirection.Forward);
        exposureStep.OnEnter(context, NavigationDirection.Forward);

        using var orchestrator = new WizardOrchestrator([exposureStep, step], context);

        // First run — daemon start fails because DaemonManager is null
        await step.RunWithOrchestrator(orchestrator);
        Assert.True(step.IsComplete.Value);
        Assert.False(step.IsRunning.Value);

        // Simulate going back and re-entering
        step.OnEnter(context, NavigationDirection.Forward);

        Assert.False(step.IsComplete.Value);
        Assert.False(step.IsRunning.Value);
        Assert.Empty(step.Results);

        // Second run should execute (not blocked by stale IsComplete)
        await step.RunWithOrchestrator(orchestrator);
        Assert.True(step.IsComplete.Value);
    }

    [Fact]
    public async Task RunWithOrchestrator_RunningDaemon_AppliesConfigViaWatcher_NotByStoppingOrRestarting()
    {
        // Watcher-owned reload: a running daemon reloads in-process when config is written
        // (its ConfigWatcherService restarts it). The wizard must just write config and
        // poll /health/ready — never stop the daemon, never POST a restart itself (#1279).
        //
        // Hold the lock so GetStatus() reports running (lock held → running) without a real
        // netclawd process. The fake daemon reports a monotonically-increasing restart
        // generation on each readiness probe, so the pre-write capture sees an older value
        // than the post-reload poll — exercising the "newer generation + healthy → ready"
        // gate end-to-end (#1302).
        using var lockHolder = new FileStream(
            _paths.LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var daemonManager = new DaemonManager(_paths, TimeProvider.System);

        // Fake daemon: healthy, advancing its reported generation on each /health/ready —
        // pre-write capture → "1", first post-write poll → "2" > 1 → restarted.
        var generation = 0;
        var handler = new StubHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (req.RequestUri!.AbsolutePath == "/api/health/ready")
            {
                generation++;
                response.Headers.Add("X-Netclaw-Generation", generation.ToString(CultureInfo.InvariantCulture));
            }
            return response;
        });
        var daemonApi = new DaemonApi(new StubHttpClientFactory(handler), new ConfigurationBuilder().Build(), _paths);

        using var step = new HealthCheckStepViewModel(
            daemonManager,
            daemonApi,
            navigationState: new ChatNavigationState(),
            timeProvider: TimeProvider.System);
        string? launchedRoute = null;
        step.Navigate = route => launchedRoute = route;
        using var exposureStep = new ExposureModeStepViewModel { SelectedMode = ExposureMode.Local };
        using var context = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };

        step.OnEnter(context, NavigationDirection.Forward);
        exposureStep.OnEnter(context, NavigationDirection.Forward);
        using var orchestrator = new WizardOrchestrator([exposureStep, step], context);

        await step.RunWithOrchestrator(orchestrator);

        Assert.True(File.Exists(_paths.NetclawConfigPath));
        Assert.Contains(step.Results, r => r.Label == "Daemon ready" && r.Passed == true);
        // A clean bootstrap launches chat automatically — no second Enter required.
        Assert.True(step.Succeeded.Value);
        Assert.Equal("/chat", launchedRoute);
        // It confirmed readiness by polling health (not by spawning/POSTing).
        Assert.Contains("GET /api/health/ready", handler.Requests);
        // Watcher-owned: the wizard never stops the daemon and never triggers the restart itself.
        Assert.DoesNotContain(step.Results, r => r.Label.Contains("Stopping daemon", StringComparison.Ordinal));
        Assert.DoesNotContain("POST /api/lifecycle/restart", handler.Requests);
    }

    [Fact]
    public void IsRestartedGeneration_BlocksStale_AllowsNewerOrDownDaemon()
    {
        // The generation gate is what prevents the readiness race: a healthy probe
        // against the still-draining pre-restart daemon (same generation) must NOT count
        // as ready (#1302).
        // Same generation as before the write → daemon hasn't restarted → not ready.
        Assert.False(HealthCheckStepViewModel.IsRestartedGeneration(before: 1, current: 1));
        // Reported generation is newer than the pre-write value → restarted → ready.
        Assert.True(HealthCheckStepViewModel.IsRestartedGeneration(before: 1, current: 2));
        // Daemon was down before the write (no baseline) → any live instance counts.
        Assert.True(HealthCheckStepViewModel.IsRestartedGeneration(before: null, current: 5));
        // Running before, but the daemon reported no generation (pre-#1302 / torn read) →
        // cannot confirm a restart → not ready yet (fail safe).
        Assert.False(HealthCheckStepViewModel.IsRestartedGeneration(before: 1, current: null));
    }

    [Fact]
    public async Task RunWithOrchestrator_SupervisorMarkerSetButNoSupervisor_SurfacesActionableReason()
    {
        // NETCLAW_CONTAINER_SUPERVISOR is set (IsExternallySupervised) but nothing actually
        // starts the daemon — e.g. a derived image that kept the marker yet replaced the
        // entrypoint with `sleep infinity`. DaemonManager.Start() defers to the (absent)
        // supervisor and the daemon never comes up; the readiness check must surface that
        // actionable reason, not the generic "Daemon did not become ready".
        var daemonManager = new DaemonManager(_paths, TimeProvider.System, new FakeSupervisor(supervised: true));

        using var step = new HealthCheckStepViewModel(
            daemonManager,
            // No readiness probe → the poll loop is skipped and we fall straight through to
            // the timeout diagnostic, exercising the message path without a real wait.
            daemonApi: null,
            navigationState: new ChatNavigationState());
        var launched = false;
        step.Navigate = _ => launched = true;
        using var exposureStep = new ExposureModeStepViewModel { SelectedMode = ExposureMode.Local };
        using var context = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };

        step.OnEnter(context, NavigationDirection.Forward);
        exposureStep.OnEnter(context, NavigationDirection.Forward);
        using var orchestrator = new WizardOrchestrator([exposureStep, step], context);

        await step.RunWithOrchestrator(orchestrator);

        var failure = Assert.Single(step.Results, r => r.Passed is false);
        Assert.Contains("container supervisor", failure.Label, StringComparison.Ordinal);
        Assert.Contains("marker may be set without a supervisor present", failure.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Daemon did not become ready", failure.Label, StringComparison.Ordinal);
        Assert.False(step.Succeeded.Value);
        // A failed health check must NOT auto-launch chat — it stays on the summary.
        Assert.False(launched);
    }

    [Fact]
    public async Task RunWithOrchestrator_UnexpectedStepException_ReleasesWizardAndReportsError()
    {
        // An unexpected exception in a step's ContributeHealthChecksAsync must NOT leave the wizard
        // wedged at IsRunning=true / IsComplete=false (GoNext gates on both being false, so the
        // operator could neither advance, go back, nor see an error).
        var daemonManager = new DaemonManager(_paths, TimeProvider.System);
        using var step = new HealthCheckStepViewModel(
            daemonManager,
            daemonApi: null,
            navigationState: new ChatNavigationState());
        using var throwingStep = new ThrowingHealthCheckStep();
        using var context = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };
        step.OnEnter(context, NavigationDirection.Forward);

        using var orchestrator = new WizardOrchestrator([throwingStep, step], context);

        // Must not throw — the catch-all handles it.
        await step.RunWithOrchestrator(orchestrator);

        Assert.False(step.IsRunning.Value);
        Assert.True(step.IsComplete.Value);
        Assert.Contains(step.Results, r => r.Passed == false && r.Label.Contains("Health check failed", StringComparison.OrdinalIgnoreCase));
    }

    // Minimal wizard step whose health-check contribution throws an unexpected (non-cancellation)
    // exception, to prove the orchestrator run releases the wizard instead of wedging it.
    private sealed class ThrowingHealthCheckStep : IWizardStepViewModel
    {
        public string StepId => "throwing";
        public string DisplayTitle => "Throwing";
        public bool IsApplicable(WizardContext context) => true;
        public int CurrentSubStep => 0;
        public int SubStepCount => 1;
        public string GetHelpText() => string.Empty;
        public bool TryAdvance() => false;
        public bool TryGoBack() => false;
        public void OnEnter(WizardContext context, NavigationDirection direction) { }
        public void OnLeave() { }
        public void ContributeConfig(WizardConfigBuilder builder) { }
        public void ContributeSecrets(WizardSecretsBuilder builder) { }
        public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
            => throw new InvalidOperationException("boom");
        public void Dispose() { }
    }

    private sealed class FakeSupervisor(bool supervised) : IContainerSupervisor
    {
        public bool IsExternallySupervised => supervised;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(responder(request));
        }
    }
}
