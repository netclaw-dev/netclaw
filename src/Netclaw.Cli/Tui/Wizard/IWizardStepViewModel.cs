using R3;

namespace Netclaw.Cli.Tui.Wizard;

/// <summary>
/// Direction of navigation into a wizard step.
/// </summary>
public enum NavigationDirection
{
    /// <summary>Entering this step from the previous step (forward progression).</summary>
    Forward,

    /// <summary>Returning to this step from the next step (backward navigation).</summary>
    Back
}

/// <summary>
/// A self-contained wizard step that owns its state, validation,
/// sub-step navigation, and config contribution.
/// </summary>
public interface IWizardStepViewModel : IDisposable
{
    /// <summary>Unique step identifier (e.g., "provider", "slack", "security-posture").</summary>
    string StepId { get; }

    /// <summary>Human-readable title for the step indicator bar.</summary>
    string DisplayTitle { get; }

    /// <summary>
    /// Whether this step should be included in the current wizard run.
    /// Re-evaluated by the orchestrator on each step transition.
    /// </summary>
    bool IsApplicable(WizardContext context);

    /// <summary>Current sub-step index (0-based). Steps with no sub-steps always return 0.</summary>
    int CurrentSubStep { get; }

    /// <summary>Total number of sub-steps. Returns 1 for steps with no sub-steps.</summary>
    int SubStepCount { get; }

    /// <summary>Help text for the current sub-step state.</summary>
    string GetHelpText();

    /// <summary>
    /// Attempt to advance within the step (next sub-step or validation trigger).
    /// Returns <c>true</c> if the step handled the advance internally (sub-step change).
    /// Returns <c>false</c> when the step is complete and the orchestrator should move forward.
    /// </summary>
    bool TryAdvance();

    /// <summary>
    /// Attempt to go back within the step (previous sub-step).
    /// Returns <c>true</c> if the step handled the back internally.
    /// Returns <c>false</c> when at the first sub-step and the orchestrator should go to the previous step.
    /// </summary>
    bool TryGoBack();

    /// <summary>
    /// Called when entering this step.
    /// When <paramref name="direction"/> is <see cref="NavigationDirection.Back"/>,
    /// the step should resume at its last sub-step. When <see cref="NavigationDirection.Forward"/>,
    /// it should start at sub-step 0.
    /// </summary>
    void OnEnter(WizardContext context, NavigationDirection direction);

    /// <summary>Called when leaving this step.</summary>
    void OnLeave();

    /// <summary>
    /// Contribute this step's configuration to the builder.
    /// Called during the finalization/health check phase.
    /// </summary>
    void ContributeConfig(WizardConfigBuilder builder);

    /// <summary>
    /// Contribute this step's secrets to the builder.
    /// Called during the finalization/health check phase.
    /// </summary>
    void ContributeSecrets(WizardSecretsBuilder builder);

    /// <summary>
    /// Run health checks specific to this step.
    /// </summary>
    Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct);
}
