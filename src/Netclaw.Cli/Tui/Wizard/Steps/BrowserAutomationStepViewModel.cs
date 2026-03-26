using Netclaw.Cli.Mcp;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for enabling browser automation and selecting the MCP backend.
/// Two sub-steps: enable/disable, then backend selection.
/// </summary>
public sealed class BrowserAutomationStepViewModel : IWizardStepViewModel
{
    private int _currentSubStep;
    private int _completedSubStep;

    public string StepId => "browser-automation";
    public string DisplayTitle => "Browser Automation";

    public bool Enabled { get; set; }
    public BrowserAutomationBackend SelectedBackend { get; set; } = BrowserAutomationBackend.Playwright;
    public bool IsChromeDevToolsAvailable { get; }
    public string ChromeDevToolsUnavailableReason { get; }

    public BrowserAutomationStepViewModel()
    {
        var detection = BrowserAutomationRuntimeDetector.DetectChrome();
        IsChromeDevToolsAvailable = detection.IsInstalled;
        ChromeDevToolsUnavailableReason = detection.Reason ?? "local Chrome executable not found";
    }

    /// <summary>Test constructor.</summary>
    internal BrowserAutomationStepViewModel(bool chromeAvailable, string chromeReason)
    {
        IsChromeDevToolsAvailable = chromeAvailable;
        ChromeDevToolsUnavailableReason = chromeReason;
    }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;
    public int SubStepCount => Enabled ? 2 : 1;

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Optional. Enable this to let the agent delegate browser steering via MCP tools.",
        1 => "  Playwright MCP is the default no-sudo path. Chrome DevTools is enabled only when a local Chrome executable is detected.",
        _ => ""
    };

    public bool TryAdvance()
    {
        if (_currentSubStep == 0 && Enabled)
        {
            _currentSubStep = 1;
            _completedSubStep = 1;
            return true;
        }
        return false;
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
            _currentSubStep = _completedSubStep;
        else
            _currentSubStep = 0;
    }

    public void OnLeave() { }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (Enabled)
        {
            builder.BrowserAutomation = new BrowserAutomationConfigSection
            {
                Enabled = true,
                Backend = SelectedBackend
            };
        }
    }

    public void ContributeSecrets(WizardSecretsBuilder builder) { }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        // Browser bootstrap health check will be added when HealthCheck step is extracted
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
