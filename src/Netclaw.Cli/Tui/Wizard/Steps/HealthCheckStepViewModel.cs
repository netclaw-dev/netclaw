// -----------------------------------------------------------------------
// <copyright file="HealthCheckStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using R3;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Final wizard step: runs health checks, writes config, starts daemon.
/// No sub-steps — the entire step is the finalization sequence.
/// </summary>
public sealed class HealthCheckStepViewModel : IWizardStepViewModel
{
    private static readonly TimeSpan OverallHealthCheckTimeout = TimeSpan.FromMinutes(5);

    // Generous enough to absorb the daemon's in-process config-reload restart (the
    // config watcher debounces ~500ms, then drains sessions before restarting) and,
    // when the daemon was down, a container supervisor's crash-loop backoff (caps at 60s).
    private static readonly TimeSpan ReloadReadyTimeout = TimeSpan.FromSeconds(90);

    private const string NotReadyMessage = "Daemon did not become ready (personality setup skipped)";

    private readonly DaemonManager? _daemonManager;
    private readonly DaemonApi? _daemonApi;
    private readonly ChatNavigationState? _navigationState;
    private readonly TimeProvider _timeProvider;
    private WizardContext? _context;

    public HealthCheckStepViewModel(
        DaemonManager? daemonManager = null,
        DaemonApi? daemonApi = null,
        ChatNavigationState? navigationState = null,
        TimeProvider? timeProvider = null)
    {
        _daemonManager = daemonManager;
        _daemonApi = daemonApi;
        _navigationState = navigationState;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string StepId => WizardStepIds.HealthCheck;
    public string DisplayTitle => "Health Check";

    // ── Reactive state ──
    public ReactiveProperty<bool> IsRunning { get; } = new(false);
    public ReactiveProperty<bool> IsComplete { get; } = new(false);

    /// <summary>True once the check completed with all probes passing. Drives the
    /// post-flight UX: a clean bootstrap shows the "ready" summary and launches chat on
    /// Enter; warnings/failures stay on the summary and exit on Enter.</summary>
    public ReactiveProperty<bool> Succeeded { get; } = new(false);
    public List<HealthCheckItem> Results { get; } = [];
    internal ReactiveProperty<int> ResultVersion { get; } = new(0);

    // All Results access is synchronized on the list instance: the async health-check core and its
    // daemon-poll timer mutate Results off the UI thread while the render thread reads it (through
    // ResultsSnapshot). HealthCheckRunner locks the same object for its Add/UpdateLast.
    private void AddResult(HealthCheckItem item)
    {
        lock (Results)
            Results.Add(item);
    }

    private void ClearResults()
    {
        lock (Results)
            Results.Clear();
    }

    private void SetLastResult(HealthCheckItem item)
    {
        lock (Results)
        {
            if (Results.Count > 0)
                Results[^1] = item;
        }
    }

    private bool LastResultPending()
    {
        lock (Results)
            return Results.Count > 0 && Results[^1].Passed is null;
    }

    /// <summary>Thread-safe snapshot for the render thread; Results is mutated off the UI thread.</summary>
    internal IReadOnlyList<HealthCheckItem> ResultsSnapshot()
    {
        lock (Results)
            return Results.ToArray();
    }

    /// <summary>Task that completes when health check finishes. For testing.</summary>
    internal Task? HealthCheckCompletion { get; private set; }

    /// <summary>Navigate callback to transition to chat after success.</summary>
    public Action<string>? Navigate { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => 0;
    public int SubStepCount => 1;

    public string GetHelpText() => "  Validating your configuration...";

    public bool TryAdvance()
    {
        // Trigger health check on Enter
        if (!IsRunning.Value && !IsComplete.Value)
            HealthCheckCompletion = RunHealthCheckAsync();
        return true; // always handled internally (we don't advance past health check)
    }

    public bool TryGoBack() => false;

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        _context = context;

        if (direction == NavigationDirection.Forward)
        {
            IsRunning.Value = false;
            IsComplete.Value = false;
            Succeeded.Value = false;
            ClearResults();
            NotifyChanged();
        }
    }

    public void OnLeave() { }

    // ── Health check does not contribute config — it writes config from all steps ──
    public void ContributeConfig(WizardConfigBuilder builder) { }
    public void ContributeSecrets(WizardSecretsBuilder builder) { }
    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Start the health check with orchestrator support. Sets <see cref="HealthCheckCompletion"/>
    /// and runs asynchronously.
    /// </summary>
    public void StartWithOrchestrator(WizardOrchestrator orchestrator)
    {
        HealthCheckCompletion = RunWithOrchestrator(orchestrator);
    }

    /// <summary>
    /// Run the full health check, write config, and start daemon.
    /// The <paramref name="orchestrator"/> is used to collect config from all steps.
    /// </summary>
    public async Task RunWithOrchestrator(WizardOrchestrator orchestrator)
    {
        using var overallCts = new CancellationTokenSource(OverallHealthCheckTimeout);
        try
        {
            await RunHealthCheckCoreAsync(orchestrator, overallCts.Token);
        }
        catch (OperationCanceledException) when (overallCts.IsCancellationRequested)
        {
            AddResult(new HealthCheckItem("Health check timed out", false));
            IsRunning.Value = false;
            IsComplete.Value = true;
            NotifyChanged();
            if (_context is not null)
                _context.StatusMessage.Value = "Setup timed out. Run `netclaw daemon start` to begin.";
        }
        catch (Exception ex)
        {
            // Any unexpected failure in the health-check core (e.g. an IO error in a step's
            // ContributeHealthChecksAsync) must still release the wizard. Leaving IsRunning=true /
            // IsComplete=false permanently wedges the step — GoNext gates on !IsRunning &&
            // !IsComplete, so the operator could neither advance, go back, nor see an error.
            AddResult(new HealthCheckItem($"Health check failed: {ex.Message}", false));
            IsRunning.Value = false;
            IsComplete.Value = true;
            NotifyChanged();
            if (_context is not null)
                _context.StatusMessage.Value = "Setup health check failed. Run `netclaw daemon start` to begin.";
        }
    }

    private Task RunHealthCheckAsync()
    {
        // Standalone mode — no orchestrator. Used for testing.
        IsRunning.Value = true;
        IsComplete.Value = false;
        ClearResults();
        NotifyChanged();

        IsRunning.Value = false;
        IsComplete.Value = true;
        NotifyChanged();
        return Task.CompletedTask;
    }

    private async Task RunHealthCheckCoreAsync(WizardOrchestrator orchestrator, CancellationToken ct)
    {
        IsRunning.Value = true;
        IsComplete.Value = false;
        ClearResults();
        NotifyChanged();

        var runner = new HealthCheckRunner(Results, NotifyChanged);

        // Run health checks from all steps
        await orchestrator.RunHealthChecksAsync(runner, ct);

        // We never stop the daemon: writing the config below is the single restart
        // trigger. A running daemon's ConfigWatcherService performs a coordinated
        // in-process restart to apply it (#1279). Capture its restart generation first so
        // we can confirm the reload actually happened — the daemon advances a monotonic
        // generation on each restart and reports it on /api/health/ready, distinguishing
        // the reloaded daemon from the still-draining old one (#1302).
        // A null baseline relaxes the gate to "any live instance counts" (see
        // IsRestartedGeneration). That happens in two ways, both intentional and bounded:
        //   * the daemon was not running before (nothing to confuse with — correct);
        //   * the daemon is running but reports no generation. That only occurs against a
        //     pre-#1302 daemon during the single upgrade where a new CLI re-runs init
        //     against an old daemon that hasn't restarted yet. The config is still written
        //     to disk and applied; the only cost is the wizard declaring "ready" a beat
        //     early against the reloading daemon — cosmetic, and gone once the daemon is on
        //     a build that emits the generation header.
        var wasRunning = _daemonManager?.GetStatus().IsRunning ?? false;
        int? generationBefore = null;
        if (wasRunning && _daemonApi is not null)
        {
            try
            {
                generationBefore = (await _daemonApi.ProbeReadinessAsync(ct)).Generation;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Running per GetStatus but not answering the probe right now. Fall back to
                // "any live instance counts" (null) — at worst the readiness-race guard is
                // relaxed for this run; it never produces a false "not ready".
                generationBefore = null;
            }
        }

        // Write config
        runner.Add(new HealthCheckItem("Writing configuration", null));
        try
        {
            orchestrator.WriteConfig();

            // Write identity files from the identity step
            // (find it in the step list — it owns the identity file generation)

            runner.UpdateLast(new HealthCheckItem("Configuration written", true));
        }
        catch (Exception ex)
        {
            runner.UpdateLast(new HealthCheckItem($"Configuration write failed: {ex.Message}", false));
        }

        // Apply config if all passed. Writing config already triggered a running
        // daemon's in-process reload restart; if it wasn't running we start it
        // (guarded — under a container supervisor Start defers and the supervisor
        // starts it). Either way we wait for a freshly-restarted, healthy daemon.
        var allPassed = runner.AllPassed;
        if (allPassed)
        {
            runner.Add(new HealthCheckItem(ProgressLabel(wasRunning), null));
            var daemonOk = await StartIfNeededAndPollAsync(wasRunning, generationBefore, ct);
            if (daemonOk)
            {
                runner.UpdateLast(new HealthCheckItem("Daemon ready", true));
            }
            else if (LastResultPending())
            {
                runner.UpdateLast(new HealthCheckItem(NotReadyMessage, false));
            }
        }

        IsRunning.Value = false;
        IsComplete.Value = true;
        NotifyChanged();

        allPassed = runner.AllPassed;
        Succeeded.Value = allPassed;
        if (allPassed)
        {
            // Validation passed — launch chat automatically rather than gating on a second
            // Enter. Mirrors the provider step's async-success auto-advance: this runs on
            // the health-check task and drives navigation through the same wired Navigate
            // delegate the Enter handler used (it sets the onboarding trigger first).
            if (_context is not null)
                _context.StatusMessage.Value = "✓ Netclaw is ready — starting chat…";
            LaunchChat();
        }
        else if (_context is not null)
        {
            _context.StatusMessage.Value =
                "Setup complete with warnings. Run `netclaw daemon start`, then `netclaw chat`. Adjust settings with `netclaw config`.";
        }
    }

    /// <summary>Launch the chat experience after a successful bootstrap. Routed through
    /// the wrapped <see cref="Navigate"/> delegate so the onboarding trigger is set first.</summary>
    public void LaunchChat() => Navigate?.Invoke("/chat");

    /// <summary>
    /// Applies the freshly-written config and waits for the daemon to be ready on it.
    /// Writing config is the single restart trigger: a running daemon's
    /// <c>ConfigWatcherService</c> performs a coordinated in-process restart, so we
    /// never stop or directly restart it here. If it was NOT running we start it (on a
    /// host this spawns; under a container supervisor <see cref="DaemonManager.Start"/>
    /// defers and the supervisor starts it). Readiness requires both a healthy probe AND
    /// a newer restart generation than <paramref name="generationBefore"/>, so the
    /// still-draining pre-restart daemon is not mistaken for the reloaded one.
    /// </summary>
    // The in-progress label depends only on whether the daemon was already running;
    // shared by the initial health item and the per-second poll relabel.
    private static string ProgressLabel(bool wasRunning) =>
        wasRunning ? "Applying configuration" : "Starting daemon";

    private async Task<bool> StartIfNeededAndPollAsync(bool wasRunning, int? generationBefore, CancellationToken ct)
    {
        if (_daemonManager is null) return false;

        // Window for crash-log diagnostics if the daemon never becomes ready.
        var startedAt = _timeProvider.GetUtcNow();
        var verb = ProgressLabel(wasRunning);

        // When the daemon was down and Start() defers to a container supervisor, hold onto
        // that reason. If the supervisor never actually brings the daemon up — the marker is
        // set but no supervisor is present (e.g. a derived image that kept
        // NETCLAW_CONTAINER_SUPERVISOR but replaced the entrypoint) — the readiness poll
        // below times out, and this message is what the operator needs instead of a generic
        // "did not become ready".
        string? supervisorDeferral = null;
        if (!wasRunning)
        {
            // Nothing is running to reload the config, so start it. Guarded: under a
            // container supervisor Start defers (no spawn) and the supervisor starts it,
            // which we treat as success here and confirm via the readiness poll below.
            var result = _daemonManager.Start();
            if (!result.Success
                && !result.Message.Contains("already running", StringComparison.OrdinalIgnoreCase)
                && !result.Message.Contains("container supervisor", StringComparison.OrdinalIgnoreCase))
            {
                SetLastResult(new HealthCheckItem(
                    result.CrashLogPath is null
                        ? result.Message
                        : $"{result.Message} See crash log: {result.CrashLogPath}",
                    false));
                NotifyChanged();
                return false;
            }

            if (!result.Success && result.Message.Contains("container supervisor", StringComparison.OrdinalIgnoreCase))
                supervisorDeferral = result.Message;
        }

        // Poll until a newer generation is healthy. We never break early on "not
        // running": the daemon goes down then comes back (in-process reload restart, or
        // a supervisor restart), possibly after a backoff. With no API client we can't
        // probe readiness, so the loop is skipped and we fall through to the diagnostic.
        var api = _daemonApi;
        var deadline = _timeProvider.GetUtcNow() + ReloadReadyTimeout;
        var elapsedSeconds = 0;
        while (api is not null && _timeProvider.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();

            DaemonApi.DaemonReadiness probe;
            try
            {
                probe = await api.ProbeReadinessAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                probe = default; // daemon mid-restart / per-request timeout — keep waiting
            }

            if (probe.Healthy && IsRestartedGeneration(generationBefore, probe.Generation))
                return true;

            // Fail fast on a startup abort instead of polling the full timeout: a bad
            // config makes the (re)started daemon log "Daemon startup aborted: …" and
            // then stay down (host) or crash-loop (supervisor) — there's nothing to wait
            // for, so surface the diagnostic now.
            var abort = _daemonManager.TryReadStartupFailureFromCrashLog(startedAt, out var abortLogPath);
            if (abort is not null)
            {
                SetLastResult(new HealthCheckItem($"{abort} See crash log: {abortLogPath}", false));
                NotifyChanged();
                return false;
            }

            SetLastResult(new HealthCheckItem($"{verb} ({++elapsedSeconds}s)", null));
            NotifyChanged();
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, ct);
        }

        // Timed out: surface the startup-abort crash-log diagnostic if present, so a
        // bad-config crash-loop isn't reported as a generic "not ready".
        var crashFailure = _daemonManager.TryReadStartupFailureFromCrashLog(startedAt, out var crashLogPath);
        var failureMessage = (crashFailure, crashLogPath, supervisorDeferral) switch
        {
            (not null, _, _) => $"{crashFailure} See crash log: {crashLogPath}",
            (null, not null, _) => $"{NotReadyMessage}. See crash log: {crashLogPath}",
            // Marker set but the supervised daemon never came up: surface the actionable
            // supervisor reason ("check the container/entrypoint logs — the marker may be
            // set without a supervisor present") instead of the generic timeout message.
            (null, null, not null) => supervisorDeferral,
            _ => null
        };
        if (failureMessage is not null)
        {
            SetLastResult(new HealthCheckItem(failureMessage, false));
            NotifyChanged();
        }

        return false;
    }

    /// <summary>
    /// Whether the daemon's reported <paramref name="current"/> restart generation proves
    /// it restarted onto the freshly-written config. A missing <paramref name="before"/>
    /// (the daemon was down before the write) means any live instance qualifies; a missing
    /// <paramref name="current"/> (the daemon answered healthy but reported no generation —
    /// a pre-#1302 daemon, or a torn probe) cannot confirm a restart, so it does not yet
    /// qualify — failing safe rather than risk reporting the still-draining pre-restart
    /// daemon as ready (#1302).
    /// </summary>
    internal static bool IsRestartedGeneration(int? before, int? current) =>
        before is null || (current is { } now && now > before);

    private void NotifyChanged()
    {
        ResultVersion.Value++;
        _context?.RequestRedraw();
    }

    public void Dispose()
    {
        IsRunning.Dispose();
        IsComplete.Dispose();
        Succeeded.Dispose();
        ResultVersion.Dispose();
    }
}
