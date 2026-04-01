using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for selecting the daemon network exposure mode.
/// Two sub-steps: mode selection, then a confirmation/notice screen for non-local modes.
/// </summary>
public sealed class ExposureModeStepViewModel : IWizardStepViewModel
{
    private int _currentSubStep;
    private int _highWaterSubStep;

    public string StepId => "exposure-mode";
    public string DisplayTitle => "Network Exposure";

    /// <summary>The selected exposure mode. Defaults to <see cref="ExposureMode.Local"/>.</summary>
    public ExposureMode SelectedMode { get; set; } = ExposureMode.Local;

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;

    /// <summary>
    /// Two sub-steps when a non-local mode is selected (confirmation/notice screen);
    /// one sub-step for Local.
    /// </summary>
    public int SubStepCount => NeedsSecondStep ? 2 : 1;

    /// <summary>True when the selected mode requires a confirmation or notice screen.</summary>
    internal bool NeedsSecondStep => SelectedMode != ExposureMode.Local;

    /// <summary>True for modes that expose the daemon to the public internet.</summary>
    public bool IsHighRisk =>
        SelectedMode is ExposureMode.TailscaleFunnel or ExposureMode.CloudflareTunnel;

    public string GetHelpText() => (_currentSubStep, IsHighRisk) switch
    {
        (0, _) => "  Local is safest — daemon only reachable from this machine. Use tunnels for remote access.",
        (1, true) => "  This mode exposes your daemon beyond your tailnet. Ensure hub authentication is configured.",
        (1, false) => "  Tailscale Serve limits access to your tailnet only. Press Enter to confirm.",
        _ => ""
    };

    public bool TryAdvance()
    {
        if (_currentSubStep == 0 && NeedsSecondStep)
        {
            _currentSubStep = 1;
            _highWaterSubStep = 1;
            return true; // handled internally
        }
        return false; // step complete, orchestrator advances
    }

    public bool TryGoBack()
    {
        if (_currentSubStep > 0)
        {
            _currentSubStep--;
            return true;
        }
        return false;
    }

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        if (direction == NavigationDirection.Back)
            _currentSubStep = _highWaterSubStep;
        else
            _currentSubStep = 0;
    }

    public void OnLeave() { }

    /// <summary>
    /// Writes the Daemon section only when exposure mode is non-default (non-local).
    /// Local mode is the schema default and is omitted to keep configs minimal.
    /// </summary>
    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (SelectedMode != ExposureMode.Local)
        {
            builder.Daemon = new DaemonConfigSection
            {
                ExposureMode = SelectedMode
            };
        }
    }

    public void ContributeSecrets(WizardSecretsBuilder builder) { }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
        => Task.CompletedTask;

    public void Dispose() { }
}
