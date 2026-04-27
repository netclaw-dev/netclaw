using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for selecting which deployment-wide features are enabled.
/// Only shown for Team and Public postures (not Personal).
/// </summary>
public sealed class FeatureSelectionStepViewModel : IWizardStepViewModel
{
    private WizardContext? _context;
    private readonly bool[] _enabledFlags = new bool[6];

    /// <summary>Feature names in display order.</summary>
    internal static readonly string[] FeatureNames =
    [
        "Memory",
        "Search",
        "Skills",
        "Scheduling",
        "SubAgents",
        "Webhooks"
    ];

    /// <summary>Feature descriptions in display order.</summary>
    internal static readonly string[] FeatureDescriptions =
    [
        "Cross-session recall and knowledge storage",
        "Web search and URL fetching",
        "Skill sync and skill file loading",
        "Reminders and scheduled tasks",
        "Delegate tasks to specialist agents",
        "Inbound webhook processing"
    ];

    public string StepId => "feature-selection";
    public string DisplayTitle => "Feature Selection";

    public bool IsApplicable(WizardContext context) =>
        context.SelectedPosture != DeploymentPosture.Personal;

    public int CurrentSubStep => 0;
    public int SubStepCount => 1;

    public string GetHelpText() =>
        "  Space to toggle features, Enter to continue. Disabling a feature removes it from all audiences.";

    /// <summary>Whether the feature at the given index is enabled.</summary>
    public bool IsFeatureEnabled(int index) => _enabledFlags[index];

    /// <summary>Toggle the enabled state of the feature at the given index.</summary>
    public void ToggleFeature(int index) => _enabledFlags[index] = !_enabledFlags[index];

    /// <summary>The current deployment posture, for view-layer annotations.</summary>
    internal DeploymentPosture? CurrentPosture => _context?.SelectedPosture;

    public bool TryAdvance()
    {
        // Single sub-step — always complete
        return false;
    }

    public bool TryGoBack()
    {
        // Single sub-step — orchestrator handles going to previous step
        return false;
    }

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        _context = context;

        if (direction == NavigationDirection.Forward)
        {
            // Set defaults based on posture
            var allOn = context.SelectedPosture == DeploymentPosture.Team;
            Array.Fill(_enabledFlags, allOn);
        }
    }

    public void OnLeave()
    {
        if (_context is not null)
        {
            _context.FeatureSelections = new FeatureSelections
            {
                MemoryEnabled = _enabledFlags[0],
                SearchEnabled = _enabledFlags[1],
                SkillsEnabled = _enabledFlags[2],
                SchedulingEnabled = _enabledFlags[3],
                SubAgentsEnabled = _enabledFlags[4],
                WebhooksEnabled = _enabledFlags[5]
            };
        }
    }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        builder.FeatureSelections = new FeatureSelectionsConfigSection
        {
            MemoryEnabled = _enabledFlags[0],
            SearchEnabled = _enabledFlags[1],
            SkillsEnabled = _enabledFlags[2],
            SchedulingEnabled = _enabledFlags[3],
            SubAgentsEnabled = _enabledFlags[4],
            WebhooksEnabled = _enabledFlags[5]
        };
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        // No secrets for feature selection
    }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        // No health check — feature selection is always valid
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
