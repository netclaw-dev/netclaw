// -----------------------------------------------------------------------
// <copyright file="SecurityPostureStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class SecurityPostureStepViewModelTests : WizardStepTestBase
{

    [Fact]
    public void OnLeave_PublishesPostureToContext()
    {
        using var step = new SecurityPostureStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);
        step.SelectedPosture = DeploymentPosture.Team;

        step.OnLeave();

        Assert.Equal(DeploymentPosture.Team, Context.SelectedPosture);
    }

    [Theory]
    [InlineData(DeploymentPosture.Personal, ShellExecutionMode.HostAllowed)]
    [InlineData(DeploymentPosture.Team, ShellExecutionMode.Off)]
    [InlineData(DeploymentPosture.Public, ShellExecutionMode.Off)]
    public void ContributeConfig_SetsShellModeByPosture(DeploymentPosture posture, ShellExecutionMode expectedShellMode)
    {
        using var step = new SecurityPostureStepViewModel();
        step.SelectedPosture = posture;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Equal(posture, builder.Security.DeploymentPosture);
        Assert.Equal(expectedShellMode, builder.Security.ShellExecutionMode);
    }

    [Fact]
    public void ContributeConfig_NullPosture_DefaultsToPersonal()
    {
        using var step = new SecurityPostureStepViewModel();

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Equal(DeploymentPosture.Personal, builder.Security.DeploymentPosture);
        Assert.Equal(ShellExecutionMode.HostAllowed, builder.Security.ShellExecutionMode);
    }

    [Fact]
    public void ContributeConfig_SetsToolConfig()
    {
        using var step = new SecurityPostureStepViewModel();
        step.SelectedPosture = DeploymentPosture.Personal;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Tools);
        Assert.Equal(ShellExecutionMode.HostAllowed, builder.Tools.ShellMode);
    }

    [Fact]
    public void ContributeConfig_Personal_WritesExplicitShellApprovalPolicy()
    {
        using var step = new SecurityPostureStepViewModel();
        step.SelectedPosture = DeploymentPosture.Personal;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Tools?.AudienceProfiles.Personal.ApprovalPolicy);
        Assert.Equal(
            ToolApprovalMode.Approval,
            builder.Tools!.AudienceProfiles.Personal.ApprovalPolicy!.GetEffectiveMode("shell_execute"));
    }

    [Fact]
    public void ContributeConfig_Team_DoesNotWritePersonalShellApprovalPolicy()
    {
        using var step = new SecurityPostureStepViewModel();
        step.SelectedPosture = DeploymentPosture.Team;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Tools?.AudienceProfiles.Personal.ApprovalPolicy);
    }
}
