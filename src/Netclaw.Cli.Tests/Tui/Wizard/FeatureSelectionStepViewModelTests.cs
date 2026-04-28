// -----------------------------------------------------------------------
// <copyright file="FeatureSelectionStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class FeatureSelectionStepViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WizardContext _context;

    public FeatureSelectionStepViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        var paths = new NetclawPaths(_tempDir);
        paths.EnsureDirectoriesExist();

        _context = new WizardContext
        {
            Paths = paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };
    }

    public void Dispose()
    {
        _context.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void IsApplicable_Personal_ReturnsFalse()
    {
        _context.SelectedPosture = DeploymentPosture.Personal;
        using var step = new FeatureSelectionStepViewModel();

        Assert.False(step.IsApplicable(_context));
    }

    [Theory]
    [InlineData(DeploymentPosture.Team)]
    [InlineData(DeploymentPosture.Public)]
    public void IsApplicable_TeamAndPublic_ReturnsTrue(DeploymentPosture posture)
    {
        _context.SelectedPosture = posture;
        using var step = new FeatureSelectionStepViewModel();

        Assert.True(step.IsApplicable(_context));
    }

    [Fact]
    public void OnEnter_PublicPosture_AllFeaturesDefaultOff()
    {
        _context.SelectedPosture = DeploymentPosture.Public;
        using var step = new FeatureSelectionStepViewModel();

        step.OnEnter(_context, NavigationDirection.Forward);

        for (var i = 0; i < FeatureSelectionStepViewModel.FeatureNames.Length; i++)
        {
            Assert.False(step.IsFeatureEnabled(i),
                $"Feature '{FeatureSelectionStepViewModel.FeatureNames[i]}' should be OFF for Public posture");
        }
    }

    [Fact]
    public void OnEnter_TeamPosture_AllFeaturesDefaultOn()
    {
        _context.SelectedPosture = DeploymentPosture.Team;
        using var step = new FeatureSelectionStepViewModel();

        step.OnEnter(_context, NavigationDirection.Forward);

        for (var i = 0; i < FeatureSelectionStepViewModel.FeatureNames.Length; i++)
        {
            Assert.True(step.IsFeatureEnabled(i),
                $"Feature '{FeatureSelectionStepViewModel.FeatureNames[i]}' should be ON for Team posture");
        }
    }

    [Fact]
    public void OnEnter_Backward_DoesNotResetFlags()
    {
        _context.SelectedPosture = DeploymentPosture.Public;
        using var step = new FeatureSelectionStepViewModel();

        // Enter forward (all off for Public)
        step.OnEnter(_context, NavigationDirection.Forward);
        // Toggle memory on
        step.ToggleFeature(0);
        Assert.True(step.IsFeatureEnabled(0));

        // Re-enter backward — should preserve manual toggles
        step.OnEnter(_context, NavigationDirection.Back);
        Assert.True(step.IsFeatureEnabled(0));
    }

    [Fact]
    public void ContributeConfig_WritesEnabledFlags_MatchingToggles()
    {
        _context.SelectedPosture = DeploymentPosture.Public;
        using var step = new FeatureSelectionStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        // All off by default for Public — selectively enable memory and scheduling
        step.ToggleFeature(0); // Memory
        step.ToggleFeature(3); // Scheduling

        var builder = new WizardConfigBuilder(_context.Paths);
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
        _context.SelectedPosture = DeploymentPosture.Team;
        using var step = new FeatureSelectionStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        // Team defaults all on — disable SubAgents
        step.ToggleFeature(4);

        var builder = new WizardConfigBuilder(_context.Paths);
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
        _context.SelectedPosture = DeploymentPosture.Public;
        using var step = new FeatureSelectionStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        // Enable search only
        step.ToggleFeature(1);
        step.OnLeave();

        Assert.NotNull(_context.FeatureSelections);
        Assert.False(_context.FeatureSelections!.MemoryEnabled);
        Assert.True(_context.FeatureSelections.SearchEnabled);
        Assert.False(_context.FeatureSelections.SkillsEnabled);
        Assert.False(_context.FeatureSelections.SchedulingEnabled);
        Assert.False(_context.FeatureSelections.SubAgentsEnabled);
        Assert.False(_context.FeatureSelections.WebhooksEnabled);
    }

    private static void AssertSectionEnabled(Dictionary<string, object> config, string sectionKey, bool expected)
    {
        Assert.True(config.ContainsKey(sectionKey), $"Config should contain '{sectionKey}' section");
        var section = (Dictionary<string, object>)config[sectionKey];
        Assert.Equal(expected, section["Enabled"]);
    }
}
