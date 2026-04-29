// -----------------------------------------------------------------------
// <copyright file="ExternalSkillsStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for detecting and enabling external skill directories.
/// Sub-step 0: checklist of detected well-known sources.
/// Sub-step 1: optional custom path text input.
/// Sub-step 2 (conditional): symlink toggle for the custom path.
/// </summary>
public sealed class ExternalSkillsStepViewModel : IWizardStepViewModel
{
    private int _currentSubStep;
    private int _highWaterSubStep;

    private readonly IReadOnlyList<WellKnownProbeResult> _detectedSources;
    private readonly bool[] _enabledFlags;

    public string StepId => WizardStepIds.ExternalSkills;
    public string DisplayTitle => "External Skills";

    /// <summary>Custom skill directory path (optional, entered in sub-step 1).</summary>
    public string? CustomPath { get; set; }

    /// <summary>Whether to allow symlinks in the custom path directory.</summary>
    public bool CustomPathAllowSymlinks { get; set; }

    public ExternalSkillsStepViewModel()
    {
        _detectedSources = ExternalSkillsConfig.ProbeWellKnownSources();
        _enabledFlags = new bool[_detectedSources.Count];
        Array.Fill(_enabledFlags, true);
    }

    /// <summary>Test constructor for injecting fake probe results.</summary>
    internal ExternalSkillsStepViewModel(IReadOnlyList<WellKnownProbeResult> detectedSources)
    {
        _detectedSources = detectedSources;
        _enabledFlags = new bool[_detectedSources.Count];
        Array.Fill(_enabledFlags, true);
    }

    /// <summary>Well-known sources detected on disk.</summary>
    public IReadOnlyList<WellKnownProbeResult> DetectedSources => _detectedSources;

    /// <summary>Whether the source at the given index is enabled.</summary>
    public bool IsSourceEnabled(int index) => _enabledFlags[index];

    /// <summary>Toggle the enabled state of the source at the given index.</summary>
    public void ToggleSource(int index) => _enabledFlags[index] = !_enabledFlags[index];

    public bool IsApplicable(WizardContext context) => _detectedSources.Count > 0;

    public int CurrentSubStep => _currentSubStep;

    public int SubStepCount => HasCustomPath ? 3 : 2;

    private bool HasCustomPath => !string.IsNullOrWhiteSpace(CustomPath);

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Use Space to toggle, Enter to confirm. Detected skill directories from other AI tools.",
        1 => "  Optional. Enter a path to a shared team skill directory, or press Enter to skip.",
        2 => "  Some skill directories use symlinks. Allow symlinks for this custom path?",
        _ => ""
    };

    public bool TryAdvance()
    {
        if (_currentSubStep == 0)
        {
            _currentSubStep = 1;
            _highWaterSubStep = 1;
            return true;
        }

        if (_currentSubStep == 1 && HasCustomPath)
        {
            _currentSubStep = 2;
            _highWaterSubStep = 2;
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
            _currentSubStep = _highWaterSubStep;
        else
            _currentSubStep = 0;
    }

    public void OnLeave() { }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        var sources = new List<ExternalSkillSource>();

        for (var i = 0; i < _detectedSources.Count; i++)
        {
            var probe = _detectedSources[i];
            sources.Add(new ExternalSkillSource
            {
                Name = probe.WellKnownAlias,
                WellKnown = probe.WellKnownAlias,
                Enabled = _enabledFlags[i],
                AllowSymlinks = probe.DefaultAllowSymlinks
            });
        }

        if (HasCustomPath)
        {
            sources.Add(new ExternalSkillSource
            {
                Name = "custom",
                Path = CustomPath,
                Enabled = true,
                AllowSymlinks = CustomPathAllowSymlinks
            });
        }

        if (sources.Count > 0)
        {
            builder.ExternalSkillSources = sources;
        }
    }

    public void ContributeSecrets(WizardSecretsBuilder builder) { }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
        => Task.CompletedTask;

    public void Dispose() { }
}
