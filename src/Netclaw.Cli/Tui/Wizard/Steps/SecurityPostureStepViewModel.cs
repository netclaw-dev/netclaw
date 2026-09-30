// -----------------------------------------------------------------------
// <copyright file="SecurityPostureStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Sections;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for selecting the deployment security posture (Personal/Team/Public).
/// Single sub-step, no async operations.
/// </summary>
public sealed class SecurityPostureStepViewModel : IWizardStepViewModel, ISectionEditor
{
    private WizardContext? _context;

    public string StepId => WizardStepIds.SecurityPosture;
    public string DisplayTitle => "Security Posture";
    public string SectionId => StepId;
    public string DisplayName => DisplayTitle;
    public string? Category => "Security & Access";
    public bool ShowInMenu => true;
    public IReadOnlyList<string> RelevantDoctorChecks => ["Security Policy", "Tool Audience Profiles"];

    public DeploymentPosture? SelectedPosture { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => 0;
    public int SubStepCount => 1;

    public string GetHelpText() =>
        "  This sets the default trust level. You can override per-channel in the Channels step.\n" +
        "  Personal mode enables shell with approval gates — commands require user sign-off on first use.";

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
    }

    public void OnLeave()
    {
        // Publish selected posture to shared context for downstream steps
        if (_context is not null)
            _context.SelectedPosture = SelectedPosture;
    }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        var posture = SelectedPosture ?? DeploymentPosture.Personal;
        var shellMode = ShellModeFor(posture);

        builder.Security = new SecurityConfigSection
        {
            DeploymentPosture = posture,
            ShellExecutionMode = shellMode
        };

        builder.Tools = new ToolsConfigSection(shellMode);
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        // No secrets for security posture
    }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        // No health check — posture is always valid
        return Task.CompletedTask;
    }

    public SectionStatus GetStatus(WizardContext context)
        => context.SelectedPosture.HasValue || SectionEditorAudit.HasExistingConfig(context, "Security.DeploymentPosture")
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;

    public string Summary(WizardContext context)
    {
        var posture = SelectedPosture
            ?? context.SelectedPosture
            ?? ReadExistingPosture(context);

        return posture?.ToString() ?? "Not configured";
    }

    public IWizardStepViewModel CreateEditor(IServiceProvider services)
        => ActivatorUtilities.CreateInstance<SecurityPostureStepViewModel>(services);

    public SectionContribution BuildContribution(IWizardStepViewModel editor)
    {
        var vm = (SecurityPostureStepViewModel)editor;
        var posture = vm.SelectedPosture ?? DeploymentPosture.Personal;
        var shellMode = ShellModeFor(posture);

        return new SectionContribution(
        [
            new SectionFieldAction("Security.DeploymentPosture", SectionFieldActionKind.Set, posture.ToString()),
            new SectionFieldAction("Security.ShellExecutionMode", SectionFieldActionKind.Set, shellMode.ToString()),
            new SectionFieldAction("Security.StrictDefaults", SectionFieldActionKind.Set, true),
            new SectionFieldAction("Tools", SectionFieldActionKind.Set, BuildToolsDictionary(shellMode))
        ]);
    }

    private static DeploymentPosture? ReadExistingPosture(WizardContext context)
    {
        if (context.ExistingConfig is null
            || !ConfigFileHelper.TryGetPathValue(context.ExistingConfig, "Security.DeploymentPosture", out var value))
        {
            return null;
        }

        return value is string text && Enum.TryParse<DeploymentPosture>(text, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    // The posture step writes intent only: the posture in Security and the shell mode here. It
    // replaces the whole Tools section, as before, but it does not write Tools.AudienceProfiles.
    // The daemon binds the Tools section on top of the posture defaults
    // (ToolAudienceProfileDefaults.CreateProfilesForPosture), which include the Personal
    // shell_execute approval rule. A copy of a default list in netclaw.json would stop following
    // later defaults. InitOutputEquivalenceTests proves that the daemon gets the same ToolConfig.
    internal static Dictionary<string, object> BuildToolsDictionary(ShellExecutionMode shellMode)
        => new()
        {
            ["ShellMode"] = shellMode.ToString()
        };

    private static ShellExecutionMode ShellModeFor(DeploymentPosture posture)
        => posture == DeploymentPosture.Personal ? ShellExecutionMode.HostAllowed : ShellExecutionMode.Off;

    public void Dispose()
    {
        // Nothing to dispose
    }
}
