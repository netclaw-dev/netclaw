// -----------------------------------------------------------------------
// <copyright file="BrowserAutomationStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class BrowserAutomationStepViewModelTests : WizardStepTestBase
{

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void SubStepCount_MatchesEnabledState(bool enabled, int expected)
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = enabled;
        Assert.Equal(expected, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ReturnsFalse_WhenDisabled()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = false;
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryAdvance_AdvancesToBackendSelection_WhenEnabled()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = true;
        Assert.True(step.TryAdvance());
        Assert.Equal(1, step.CurrentSubStep);
    }

    [Fact]
    public void TryGoBack_FromBackend_ReturnsToEnable()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = true;
        step.TryAdvance(); // → sub-step 1

        Assert.True(step.TryGoBack());
        Assert.Equal(0, step.CurrentSubStep);
    }

    [Fact]
    public void OnEnter_Back_ResumesAtLastSubStep()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = true;
        step.TryAdvance(); // → sub-step 1

        step.OnEnter(Context, NavigationDirection.Back);
        Assert.Equal(1, step.CurrentSubStep);
    }

    [Fact]
    public void ContributeConfig_SetsBackend_WhenEnabled()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = true;
        step.SelectedBackend = BrowserAutomationBackend.Playwright;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.BrowserAutomation);
        Assert.True(builder.BrowserAutomation!.Enabled);
        Assert.Equal(BrowserAutomationBackend.Playwright, builder.BrowserAutomation.Backend);
    }

    [Fact]
    public void ContributeConfig_NoSection_WhenDisabled()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = false;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.BrowserAutomation);
    }

    [Fact]
    public void DefaultBackend_IsPlaywright()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        Assert.Equal(BrowserAutomationBackend.Playwright, step.SelectedBackend);
    }
}
