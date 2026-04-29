// -----------------------------------------------------------------------
// <copyright file="SecurityPostureStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for selecting the deployment security posture (Personal/Team/Public).
/// Single sub-step, no async operations.
/// </summary>
public sealed class SecurityPostureStepViewModel : IWizardStepViewModel
{
    private WizardContext? _context;

    public string StepId => WizardStepIds.SecurityPosture;
    public string DisplayTitle => "Security Posture";

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
        var shellMode = posture == DeploymentPosture.Personal
            ? ShellExecutionMode.HostAllowed
            : ShellExecutionMode.Off;

        builder.Security = new SecurityConfigSection
        {
            DeploymentPosture = posture,
            ShellExecutionMode = shellMode
        };

        var profiles = ToolAudienceProfileDefaults.CreateProfiles();

        // Personal posture: enable approval gates for shell by default.
        // The operator can override this in config if they want unrestricted shell.
        if (posture == DeploymentPosture.Personal)
        {
            profiles.Personal.ApprovalPolicy = new ToolApprovalConfig
            {
                ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
                {
                    ["shell_execute"] = ToolApprovalMode.Approval
                }
            };
        }

        builder.Tools = new ToolConfig
        {
            ShellMode = shellMode,
            AudienceProfiles = profiles
        };
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

    public void Dispose()
    {
        // Nothing to dispose
    }
}
