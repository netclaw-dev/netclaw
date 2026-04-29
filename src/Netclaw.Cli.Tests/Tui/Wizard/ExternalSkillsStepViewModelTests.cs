// -----------------------------------------------------------------------
// <copyright file="ExternalSkillsStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class ExternalSkillsStepViewModelTests : WizardStepTestBase
{
    private static readonly IReadOnlyList<WellKnownProbeResult> TwoSources =
    [
        new("claude-code", "Claude Code", "/home/user/.claude/skills", true),
        new("open-code", "Open Code", "/home/user/.open-code/skills", false)
    ];

    private static readonly IReadOnlyList<WellKnownProbeResult> OnlyClaudeCode =
    [
        new("claude-code", "Claude Code", "/home/user/.claude/skills", true)
    ];

    private static readonly IReadOnlyList<WellKnownProbeResult> NoSources = [];

    [Fact]
    public void IsApplicable_True_WhenSourcesDetected()
    {
        using var step = new ExternalSkillsStepViewModel(OnlyClaudeCode);
        Assert.True(step.IsApplicable(Context));
    }

    [Fact]
    public void IsApplicable_False_WhenNoSourcesDetected()
    {
        using var step = new ExternalSkillsStepViewModel(NoSources);
        Assert.False(step.IsApplicable(Context));
    }

    [Fact]
    public void AllSourcesEnabledByDefault()
    {
        using var step = new ExternalSkillsStepViewModel(TwoSources);
        Assert.True(step.IsSourceEnabled(0));
        Assert.True(step.IsSourceEnabled(1));
    }

    [Fact]
    public void ToggleSource_FlipsEnabled()
    {
        using var step = new ExternalSkillsStepViewModel(TwoSources);

        step.ToggleSource(0);
        Assert.False(step.IsSourceEnabled(0));
        Assert.True(step.IsSourceEnabled(1));

        step.ToggleSource(0);
        Assert.True(step.IsSourceEnabled(0));
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("/opt/team/skills", 3)]
    public void SubStepCount_MatchesCustomPath(string? customPath, int expected)
    {
        using var step = new ExternalSkillsStepViewModel(OnlyClaudeCode);
        if (customPath is not null) step.CustomPath = customPath;
        Assert.Equal(expected, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_FromChecklist_GoesToCustomPath()
    {
        using var step = new ExternalSkillsStepViewModel(OnlyClaudeCode);
        step.OnEnter(Context, NavigationDirection.Forward);

        Assert.True(step.TryAdvance());
        Assert.Equal(1, step.CurrentSubStep);
    }

    [Fact]
    public void TryAdvance_FromCustomPath_GoesToSymlink_WhenPathSet()
    {
        using var step = new ExternalSkillsStepViewModel(OnlyClaudeCode);
        step.OnEnter(Context, NavigationDirection.Forward);
        step.TryAdvance(); // → sub-step 1
        step.CustomPath = "/opt/team/skills";

        Assert.True(step.TryAdvance());
        Assert.Equal(2, step.CurrentSubStep);
    }

    [Fact]
    public void TryAdvance_FromCustomPath_Completes_WhenNoPath()
    {
        using var step = new ExternalSkillsStepViewModel(OnlyClaudeCode);
        step.OnEnter(Context, NavigationDirection.Forward);
        step.TryAdvance(); // → sub-step 1

        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryGoBack_FromCustomPath_ReturnsToChecklist()
    {
        using var step = new ExternalSkillsStepViewModel(OnlyClaudeCode);
        step.OnEnter(Context, NavigationDirection.Forward);
        step.TryAdvance(); // → sub-step 1

        Assert.True(step.TryGoBack());
        Assert.Equal(0, step.CurrentSubStep);
    }

    [Fact]
    public void TryGoBack_FromChecklist_ReturnsFalse()
    {
        using var step = new ExternalSkillsStepViewModel(OnlyClaudeCode);
        step.OnEnter(Context, NavigationDirection.Forward);

        Assert.False(step.TryGoBack());
    }

    [Fact]
    public void OnEnter_Back_ResumesAtLastSubStep()
    {
        using var step = new ExternalSkillsStepViewModel(OnlyClaudeCode);
        step.OnEnter(Context, NavigationDirection.Forward);
        step.TryAdvance(); // → sub-step 1

        step.OnEnter(Context, NavigationDirection.Back);
        Assert.Equal(1, step.CurrentSubStep);
    }

    [Fact]
    public void ContributeConfig_WritesEnabledSources()
    {
        using var step = new ExternalSkillsStepViewModel(TwoSources);
        step.ToggleSource(1); // disable Open Code

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.ExternalSkillSources);
        Assert.Equal(2, builder.ExternalSkillSources!.Count);

        var claude = builder.ExternalSkillSources[0];
        Assert.Equal("claude-code", claude.Name);
        Assert.Equal("claude-code", claude.WellKnown);
        Assert.True(claude.Enabled);
        Assert.True(claude.AllowSymlinks);

        var openCode = builder.ExternalSkillSources[1];
        Assert.Equal("open-code", openCode.Name);
        Assert.Equal("open-code", openCode.WellKnown);
        Assert.False(openCode.Enabled);
        Assert.False(openCode.AllowSymlinks);
    }

    [Fact]
    public void ContributeConfig_IncludesCustomPath()
    {
        using var step = new ExternalSkillsStepViewModel(OnlyClaudeCode);
        step.CustomPath = "/opt/team/skills";
        step.CustomPathAllowSymlinks = true;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.ExternalSkillSources);
        Assert.Equal(2, builder.ExternalSkillSources!.Count);

        var custom = builder.ExternalSkillSources[1];
        Assert.Equal("custom", custom.Name);
        Assert.Equal("/opt/team/skills", custom.Path);
        Assert.Null(custom.WellKnown);
        Assert.True(custom.Enabled);
        Assert.True(custom.AllowSymlinks);
    }

    [Fact]
    public void ContributeConfig_NoSection_WhenNoSourcesAndNoCustomPath()
    {
        using var step = new ExternalSkillsStepViewModel(NoSources);

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.ExternalSkillSources);
    }
}
