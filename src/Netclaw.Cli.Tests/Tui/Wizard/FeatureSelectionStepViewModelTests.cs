// -----------------------------------------------------------------------
// <copyright file="FeatureSelectionStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class FeatureSelectionStepViewModelTests : WizardStepTestBase
{
    [Fact]
    public void IsApplicable_Personal_ReturnsFalse()
    {
        Context.SelectedPosture = DeploymentPosture.Personal;
        using var step = new FeatureSelectionStepViewModel();

        Assert.False(step.IsApplicable(Context));
    }

    [Theory]
    [InlineData(DeploymentPosture.Team)]
    [InlineData(DeploymentPosture.Public)]
    public void IsApplicable_TeamAndPublic_ReturnsTrue(DeploymentPosture posture)
    {
        Context.SelectedPosture = posture;
        using var step = new FeatureSelectionStepViewModel();

        Assert.True(step.IsApplicable(Context));
    }

    [Fact]
    public void OnEnter_PublicPosture_AllFeaturesDefaultOff()
    {
        Context.SelectedPosture = DeploymentPosture.Public;
        using var step = new FeatureSelectionStepViewModel();

        step.OnEnter(Context, NavigationDirection.Forward);

        for (var i = 0; i < FeatureSelectionStepViewModel.FeatureNames.Length; i++)
        {
            Assert.False(step.IsFeatureEnabled(i),
                $"Feature '{FeatureSelectionStepViewModel.FeatureNames[i]}' should be OFF for Public posture");
        }
    }

    [Fact]
    public void OnEnter_TeamPosture_AllFeaturesDefaultOn()
    {
        Context.SelectedPosture = DeploymentPosture.Team;
        using var step = new FeatureSelectionStepViewModel();

        step.OnEnter(Context, NavigationDirection.Forward);

        for (var i = 0; i < FeatureSelectionStepViewModel.FeatureNames.Length; i++)
        {
            Assert.True(step.IsFeatureEnabled(i),
                $"Feature '{FeatureSelectionStepViewModel.FeatureNames[i]}' should be ON for Team posture");
        }
    }

    [Fact]
    public void OnEnter_Backward_DoesNotResetFlags()
    {
        Context.SelectedPosture = DeploymentPosture.Public;
        using var step = new FeatureSelectionStepViewModel();

        // Enter forward (all off for Public)
        step.OnEnter(Context, NavigationDirection.Forward);
        // Toggle memory on
        step.ToggleFeature(0);
        Assert.True(step.IsFeatureEnabled(0));

        // Re-enter backward — should preserve manual toggles
        step.OnEnter(Context, NavigationDirection.Back);
        Assert.True(step.IsFeatureEnabled(0));
    }

    [Fact]
    public void ContributeConfig_WritesEnabledFlags_MatchingToggles()
    {
        Context.SelectedPosture = DeploymentPosture.Public;
        using var step = new FeatureSelectionStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);

        // All off by default for Public — selectively enable memory and scheduling
        step.ToggleFeature(0); // Memory
        step.ToggleFeature(3); // Scheduling

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.FeatureSelections);
        Assert.True(builder.FeatureSelections!.MemoryEnabled);
        Assert.False(builder.FeatureSelections.SearchEnabled);
        Assert.False(builder.FeatureSelections.SkillsEnabled);
        Assert.True(builder.FeatureSelections.SchedulingEnabled);
        Assert.False(builder.FeatureSelections.SubAgentsEnabled);
        Assert.False(builder.FeatureSelections.WebhooksEnabled);
    }

    [Fact]
    public void ContributeConfig_MergesEnabledFlags_IntoConfigDictionary()
    {
        Context.SelectedPosture = DeploymentPosture.Team;
        using var step = new FeatureSelectionStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);

        // Team defaults all on — disable SubAgents
        step.ToggleFeature(4);

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);
        var config = builder.BuildConfigDictionary();

        // All sections should have Enabled flags
        AssertSectionEnabled(config, "Memory", true);
        AssertSectionEnabled(config, "Search", true);
        AssertSectionEnabled(config, "SkillSync", true);
        AssertSectionEnabled(config, "Scheduling", true);
        AssertSectionEnabled(config, "SubAgents", false);
        AssertSectionEnabled(config, "Webhooks", true);
    }

    [Fact]
    public void OnLeave_PublishesFeatureSelectionsToContext()
    {
        Context.SelectedPosture = DeploymentPosture.Public;
        using var step = new FeatureSelectionStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);

        // Enable search only
        step.ToggleFeature(1);
        step.OnLeave();

        Assert.NotNull(Context.FeatureSelections);
        Assert.False(Context.FeatureSelections!.MemoryEnabled);
        Assert.True(Context.FeatureSelections.SearchEnabled);
        Assert.False(Context.FeatureSelections.SkillsEnabled);
        Assert.False(Context.FeatureSelections.SchedulingEnabled);
        Assert.False(Context.FeatureSelections.SubAgentsEnabled);
        Assert.False(Context.FeatureSelections.WebhooksEnabled);
    }

    [Fact]
    public void ContributeConfig_SkippedStep_DoesNotSetFeatureSelections()
    {
        using var step = new FeatureSelectionStepViewModel();

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.FeatureSelections);
    }

    [Fact]
    public void ContributeConfig_SkippedStep_ConfigDictionary_OmitsFeatureFlags()
    {
        using var step = new FeatureSelectionStepViewModel();

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);
        var config = builder.BuildConfigDictionary();

        // Feature sections should NOT have an Enabled flag injected by the skipped step.
        // Some sections (e.g., SkillSync) exist unconditionally with other keys — the key
        // assertion is that no Enabled:false was written.
        AssertNoEnabledKey(config, "Memory");
        AssertNoEnabledKey(config, "Search");
        AssertNoEnabledKey(config, "SkillSync");
        AssertNoEnabledKey(config, "Scheduling");
        AssertNoEnabledKey(config, "SubAgents");
        AssertNoEnabledKey(config, "Webhooks");
    }

}
