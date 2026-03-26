using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for per-channel audience configuration.
/// Conditionally shown only when at least one chat service is enabled.
/// Single sub-step with custom keyboard navigation (arrow keys, a/d).
/// </summary>
public sealed class ChannelsStepViewModel : IWizardStepViewModel
{
    private WizardContext? _context;

    public string StepId => "channels";
    public string DisplayTitle => "Channels";

    /// <summary>The channel entries from the shared context, accessible by the view.</summary>
    public List<ChannelEntry> ChannelEntries => _context?.ChannelEntries ?? [];

    /// <summary>The selected posture from the shared context, for deriving audience defaults.</summary>
    public DeploymentPosture SelectedPosture => _context?.SelectedPosture ?? DeploymentPosture.Personal;

    public bool IsApplicable(WizardContext context) => context.AnyChatServicesEnabled;

    public int CurrentSubStep => 0;
    public int SubStepCount => 1;

    public string GetHelpText() =>
        "  Use \u2190/\u2192 to change audience. a to add, d to remove. Enter to continue.";

    public bool TryAdvance() => false;
    public bool TryGoBack() => false;

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        _context = context;

        // Derive security defaults on forward entry (populates channel entries)
        if (direction == NavigationDirection.Forward)
            DeriveSecurityDefaults(context);
    }

    public void OnLeave() { }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        // Channel audiences are already set on the Slack section via the SlackStep.
        // This step just allows editing them in the context's ChannelEntries list.
    }

    public void ContributeSecrets(WizardSecretsBuilder builder) { }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Derive channel entries and audience defaults from context state.
    /// </summary>
    internal static void DeriveSecurityDefaults(WizardContext context)
    {
        var posture = context.SelectedPosture ?? DeploymentPosture.Personal;

        context.ChannelEntries.Clear();

        // DM row — audience depends on allowed user count
        // (simplified: use posture default since we don't track DM state here)
        var dmAudience = posture == DeploymentPosture.Personal
            ? TrustAudience.Personal.ToWireValue()
            : posture == DeploymentPosture.Team
                ? TrustAudience.Team.ToWireValue()
                : TrustAudience.Public.ToWireValue();

        context.ChannelEntries.Add(new ChannelEntry("DMs", "dm", dmAudience, isDmRow: true));
    }

    public void Dispose() { }
}
