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

    private readonly DaemonManager? _daemonManager;
    private readonly DaemonApi? _daemonApi;
    private readonly ChatNavigationState? _navigationState;
    private WizardContext? _context;

    public HealthCheckStepViewModel(
        DaemonManager? daemonManager = null,
        DaemonApi? daemonApi = null,
        ChatNavigationState? navigationState = null)
    {
        _daemonManager = daemonManager;
        _daemonApi = daemonApi;
        _navigationState = navigationState;
    }

    public string StepId => "health-check";
    public string DisplayTitle => "Health Check";

    // ── Reactive state ──
    public ReactiveProperty<bool> IsRunning { get; } = new(false);
    public ReactiveProperty<bool> IsComplete { get; } = new(false);
    public List<HealthCheckItem> Results { get; } = [];
    internal ReactiveProperty<int> ResultVersion { get; } = new(0);

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
            Results.Add(new HealthCheckItem("Health check timed out", false));
            IsRunning.Value = false;
            IsComplete.Value = true;
            NotifyChanged();
            if (_context is not null)
                _context.StatusMessage.Value = "Setup timed out. Run `netclaw daemon start` to begin.";
        }
    }

    private Task RunHealthCheckAsync()
    {
        // Standalone mode — no orchestrator. Used for testing.
        IsRunning.Value = true;
        IsComplete.Value = false;
        Results.Clear();
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
        Results.Clear();
        NotifyChanged();

        var runner = new HealthCheckRunner(Results, NotifyChanged);

        // Run health checks from all steps
        await orchestrator.RunHealthChecksAsync(runner, ct);

        // Stop daemon before writing config
        if (_daemonManager is not null)
        {
            var status = _daemonManager.GetStatus();
            if (status.IsRunning)
            {
                runner.Add(new HealthCheckItem("Stopping daemon for config update", null));
                var stopResult = await _daemonManager.StopAsync("config-update");
                runner.UpdateLast(stopResult.Success
                    ? new HealthCheckItem("Daemon stopped", true)
                    : new HealthCheckItem($"Daemon stop failed: {stopResult.Message}", false));
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

        // Start daemon if all passed
        var allPassed = runner.AllPassed;
        if (allPassed)
        {
            runner.Add(new HealthCheckItem("Starting daemon", null));
            var daemonOk = await StartAndPollDaemonAsync(ct);
            runner.UpdateLast(daemonOk
                ? new HealthCheckItem("Daemon ready", true)
                : new HealthCheckItem("Daemon did not become ready (personality setup skipped)", false));
        }

        IsRunning.Value = false;
        IsComplete.Value = true;
        NotifyChanged();

        allPassed = runner.AllPassed;
        if (allPassed && _context is not null)
        {
            _context.StatusMessage.Value = "Setup complete! Launching chat...";
            Navigate?.Invoke("/chat");
        }
        else if (_context is not null)
        {
            _context.StatusMessage.Value = "Setup complete with warnings. Run `netclaw daemon start` to begin.";
        }
    }

    private async Task<bool> StartAndPollDaemonAsync(CancellationToken ct)
    {
        if (_daemonManager is null) return false;

        var result = _daemonManager.Start();
        if (!result.Success && !result.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
            return false;

        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (_daemonApi is not null && await _daemonApi.IsHealthyAsync(ct))
                    return true;
            }
            catch (HttpRequestException)
            {
                Results[^1] = new HealthCheckItem($"Starting daemon ({i + 1}s)", null);
                NotifyChanged();
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Results[^1] = new HealthCheckItem($"Starting daemon ({i + 1}s)", null);
                NotifyChanged();
            }

            await Task.Delay(1000, ct);
        }

        return false;
    }

    private void NotifyChanged()
    {
        ResultVersion.Value++;
        _context?.RequestRedraw();
    }

    public void Dispose()
    {
        IsRunning.Dispose();
        IsComplete.Dispose();
        ResultVersion.Dispose();
    }
}
