using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for selecting the daemon network exposure mode and inbound webhook enablement.
/// Sub-steps: mode selection → optional confirmation/notice → webhook toggle.
/// </summary>
public sealed class ExposureModeStepViewModel : IWizardStepViewModel
{
    private int _currentSubStep;
    private int _highWaterSubStep;

    public string StepId => "exposure-mode";
    public string DisplayTitle => "Network Exposure";

    /// <summary>The selected exposure mode. Defaults to <see cref="ExposureMode.Local"/>.</summary>
    public ExposureMode SelectedMode { get; set; } = ExposureMode.Local;

    /// <summary>Whether inbound webhook ingestion is enabled.</summary>
    public bool WebhooksEnabled { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;

    /// <summary>
    /// Sub-steps: mode selection + optional confirmation/notice + webhook toggle.
    /// </summary>
    public int SubStepCount => NeedsConfirmation ? 3 : 2;

    /// <summary>True when the selected mode requires a confirmation or notice screen.</summary>
    internal bool NeedsConfirmation => SelectedMode != ExposureMode.Local;

    /// <summary>True for modes that expose the daemon to the public internet.</summary>
    public bool IsHighRisk =>
        SelectedMode is ExposureMode.TailscaleFunnel or ExposureMode.CloudflareTunnel;

    /// <summary>The sub-step index for the inbound webhook toggle (always last).</summary>
    internal int WebhookSubStep => NeedsConfirmation ? 2 : 1;

    public string GetHelpText()
    {
        if (_currentSubStep == 0)
            return "  Local is safest — daemon only reachable from this machine. Use tunnels for remote access.";

        if (_currentSubStep == WebhookSubStep)
            return "  Inbound webhooks let external services trigger autonomous runs via HTTP POST.";

        // Sub-step 1 confirmation (non-Local modes only)
        return IsHighRisk
            ? "  This mode exposes your daemon beyond your tailnet. Ensure hub authentication is configured."
            : "  Tailscale Serve limits access to your tailnet only. Press Enter to confirm.";
    }

    public bool TryAdvance()
    {
        if (_currentSubStep == 0 && NeedsConfirmation)
        {
            _currentSubStep = 1;
            _highWaterSubStep = 1;
            return true; // mode selection → confirmation
        }

        if (_currentSubStep < WebhookSubStep)
        {
            _currentSubStep = WebhookSubStep;
            _highWaterSubStep = WebhookSubStep;
            return true; // → webhook toggle
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
    /// Writes the Daemon section (non-local modes) and Webhooks section (when enabled).
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

        if (WebhooksEnabled)
        {
            builder.Webhooks = new WebhooksConfigSection { Enabled = true };
        }
    }

    public void ContributeSecrets(WizardSecretsBuilder builder) { }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
        => Task.CompletedTask;

    public void Dispose() { }
}
