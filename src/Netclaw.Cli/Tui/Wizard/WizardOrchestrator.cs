// -----------------------------------------------------------------------
// <copyright file="WizardOrchestrator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Wizard.Steps;
using R3;

namespace Netclaw.Cli.Tui.Wizard;

/// <summary>
/// Thin orchestrator that manages wizard step sequencing, navigation,
/// and config finalization. Replaces the monolithic InitWizardViewModel's
/// step navigation and config writing responsibilities.
/// </summary>
public sealed class WizardOrchestrator : IDisposable
{
    private readonly IReadOnlyList<IWizardStepViewModel> _allSteps;
    private readonly WizardContext _context;
    private List<IWizardStepViewModel> _activeSteps;
    private int _currentIndex;

    public WizardOrchestrator(IReadOnlyList<IWizardStepViewModel> steps, WizardContext context)
    {
        _allSteps = steps;
        _context = context;
        _activeSteps = BuildInitialActiveSteps();

        if (_activeSteps.Count > 0)
            _activeSteps[0].OnEnter(context, NavigationDirection.Forward);
    }

    /// <summary>The currently active step, or null if no steps are active.</summary>
    public IWizardStepViewModel? CurrentStep =>
        _currentIndex >= 0 && _currentIndex < _activeSteps.Count
            ? _activeSteps[_currentIndex]
            : null;

    /// <summary>Reactive property that emits the current step index for UI binding.</summary>
    public ReactiveProperty<int> CurrentStepIndex { get; } = new(0);

    /// <summary>Number of active (non-skipped) steps in the wizard.</summary>
    public int ActiveStepCount => _activeSteps.Count;

    /// <summary>
    /// Returns the 1-based display number for the current step,
    /// accounting for skipped steps.
    /// </summary>
    public int GetDisplayStepNumber() => _currentIndex + 1;

    /// <summary>
    /// Returns the 1-based display number for a given step by its ID.
    /// Returns -1 if the step is not in the active list.
    /// </summary>
    public int GetDisplayStepNumber(string stepId)
    {
        for (var i = 0; i < _activeSteps.Count; i++)
        {
            if (_activeSteps[i].StepId == stepId)
                return i + 1;
        }
        return -1;
    }

    /// <summary>
    /// Advance the wizard. First tries to advance within the current step (sub-step).
    /// If the step reports completion, moves to the next applicable step.
    /// </summary>
    /// <returns><c>true</c> if the wizard advanced; <c>false</c> if already at the end.</returns>
    public bool GoNext()
    {
        var current = CurrentStep;
        if (current is null)
            return false;

        // Let the step handle internal advancement (sub-steps)
        if (current.TryAdvance())
        {
            _context.StatusMessage.Value = "";
            return true;
        }

        // Step is complete — move to the next applicable step.
        // Capture current position before OnLeave/rebuild can shift the index.
        var currentIdx = _currentIndex;
        current.OnLeave();
        _activeSteps = RebuildActiveSteps();

        var nextIndex = currentIdx + 1;
        if (nextIndex >= _activeSteps.Count)
            return false; // already at the end

        _currentIndex = nextIndex;
        CurrentStepIndex.Value = _currentIndex;
        _activeSteps[_currentIndex].OnEnter(_context, NavigationDirection.Forward);
        _context.StatusMessage.Value = "";
        return true;
    }

    /// <summary>
    /// Go back in the wizard. First tries to go back within the current step (sub-step).
    /// If at the first sub-step, moves to the previous applicable step and enters it
    /// with <see cref="NavigationDirection.Back"/> so it resumes at its last sub-step.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the wizard went back;
    /// <c>false</c> if already at the first step's first sub-step (caller should handle quit).
    /// </returns>
    public bool GoBack()
    {
        var current = CurrentStep;
        if (current is null)
            return false;

        // Let the step handle internal back-navigation (sub-steps)
        if (current.TryGoBack())
        {
            _context.StatusMessage.Value = "";
            return true;
        }

        // At the first sub-step — move to the previous applicable step.
        // Capture current position before OnLeave/rebuild can shift the index.
        var currentIdx = _currentIndex;
        if (currentIdx <= 0)
            return false; // at the very beginning

        current.OnLeave();
        _activeSteps = RebuildActiveSteps();

        var prevIndex = currentIdx - 1;
        if (prevIndex < 0)
            return false;

        _currentIndex = prevIndex;
        CurrentStepIndex.Value = _currentIndex;
        _activeSteps[_currentIndex].OnEnter(_context, NavigationDirection.Back);
        _context.StatusMessage.Value = "";
        return true;
    }

    /// <summary>
    /// Collect config contributions from all steps and write all config files,
    /// identity files, and seed built-in agents.
    /// </summary>
    public void WriteConfig()
    {
        _context.Paths.EnsureDirectoriesExist();

        var configBuilder = new WizardConfigBuilder(_context.Paths);
        var secretsBuilder = new WizardSecretsBuilder(_context.Paths);

        foreach (var step in _activeSteps)
        {
            step.ContributeConfig(configBuilder);
            step.ContributeSecrets(secretsBuilder);
        }

        configBuilder.WriteConfigFile();
        secretsBuilder.WriteSecretsFile();

        // Write provider credentials (deferred from ContributeSecrets to finalization)
        var providerStep = _activeSteps.OfType<ProviderStepViewModel>().FirstOrDefault();
        providerStep?.WriteProviderCredentials(_context.Paths);

        // Write identity files and seed built-in agents from the identity step
        var identityStep = _activeSteps.OfType<IdentityStepViewModel>().FirstOrDefault();
        if (identityStep is not null)
        {
            identityStep.WriteIdentityFiles(_context.Paths);
            identityStep.SeedBuiltInAgents(_context.Paths);
        }

        // Write bootstrap paired device for non-Local exposure modes so the daemon
        // can start with at least one paired device (satisfies ExposureModeValidationService).
        var exposureStep = _activeSteps.OfType<ExposureModeStepViewModel>().FirstOrDefault();
        exposureStep?.WriteBootstrapDevice(_context.Paths);
    }

    /// <summary>
    /// Run health checks from all steps.
    /// </summary>
    public async Task RunHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        foreach (var step in _activeSteps)
        {
            ct.ThrowIfCancellationRequested();
            await step.ContributeHealthChecksAsync(runner, ct);
        }
    }

    /// <summary>
    /// Build the initial active step list (called from constructor before _activeSteps is assigned).
    /// </summary>
    private List<IWizardStepViewModel> BuildInitialActiveSteps()
    {
        _currentIndex = 0;
        return [.. _allSteps.Where(s => s.IsApplicable(_context))];
    }

    /// <summary>
    /// Re-evaluate which steps are applicable based on current context.
    /// Preserves the current step's position in the list.
    /// </summary>
    private List<IWizardStepViewModel> RebuildActiveSteps()
    {
        var currentStepId = CurrentStep?.StepId;
        var active = _allSteps.Where(s => s.IsApplicable(_context)).ToList();

        // Try to preserve the current index pointing at the same step
        if (currentStepId is not null)
        {
            for (var i = 0; i < active.Count; i++)
            {
                if (active[i].StepId == currentStepId)
                {
                    _currentIndex = i;
                    return active;
                }
            }
        }

        // Current step was removed from active list — clamp index
        if (_currentIndex >= active.Count)
            _currentIndex = Math.Max(0, active.Count - 1);

        return active;
    }

    public void Dispose()
    {
        CurrentStepIndex.Dispose();
        foreach (var step in _allSteps)
            step.Dispose();
    }
}
