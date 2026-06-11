// -----------------------------------------------------------------------
// <copyright file="SlackStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class SlackStepViewModelTests : WizardStepTestBase
{
    private readonly FakeSlackProbe _fakeProbe = new();

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 6)]
    [InlineData(true, true, 7)]
    public void SubStepCount_MatchesState(bool enabled, bool restrict, int expected)
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = enabled;
        if (restrict) step.RestrictToSpecificUsers = true;
        Assert.Equal(expected, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ReturnsFalse_WhenDisabled()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = false;
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryAdvance_ThroughAllSubSteps()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;

        // Sub-step 0 → 1
        Assert.True(step.TryAdvance());
        Assert.Equal(1, step.CurrentSubStep);

        // 1 → 2
        Assert.True(step.TryAdvance());
        Assert.Equal(2, step.CurrentSubStep);

        // 2 → 3
        Assert.True(step.TryAdvance());
        Assert.Equal(3, step.CurrentSubStep);

        // 3 → 4
        Assert.True(step.TryAdvance());
        Assert.Equal(4, step.CurrentSubStep);

        // 4 → 5
        Assert.True(step.TryAdvance());
        Assert.Equal(5, step.CurrentSubStep);

        // Sub-step 5 is UserAccessChoice; default RestrictToSpecificUsers=false completes the step
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryAdvance_WithRestrict_AdvancesToAllowedUserIds()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;

        // Advance to sub-step 5 (UserAccessChoice)
        for (var i = 0; i < 5; i++)
            step.TryAdvance();
        Assert.Equal(5, step.CurrentSubStep);

        step.RestrictToSpecificUsers = true;
        Assert.True(step.TryAdvance());
        Assert.Equal(6, step.CurrentSubStep);

        // Sub-step 6 (AllowedUserIds) completes the step
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryAdvance_AllowAnyone_ClearsAllowedUserIds()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;
        step.AllowedUserIdsInput = "U01ABC123";

        // Advance to sub-step 5 (UserAccessChoice)
        for (var i = 0; i < 5; i++)
            step.TryAdvance();

        step.RestrictToSpecificUsers = false;
        Assert.False(step.TryAdvance());
        Assert.Null(step.AllowedUserIdsInput);
    }

    [Fact]
    public void TryGoBack_ThroughSubSteps()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;

        // Advance to sub-step 3
        step.TryAdvance(); // 0→1
        step.TryAdvance(); // 1→2
        step.TryAdvance(); // 2→3
        Assert.Equal(3, step.CurrentSubStep);

        // Go back
        Assert.True(step.TryGoBack()); // 3→2
        Assert.Equal(2, step.CurrentSubStep);

        Assert.True(step.TryGoBack()); // 2→1
        Assert.Equal(1, step.CurrentSubStep);

        Assert.True(step.TryGoBack()); // 1→0
        Assert.Equal(0, step.CurrentSubStep);

        // At first sub-step — orchestrator should handle
        Assert.False(step.TryGoBack());
    }

    [Fact]
    public void OnEnter_Back_ResumesAtLastSubStep()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;

        // Advance through all sub-steps
        for (var i = 0; i < 5; i++)
            step.TryAdvance();
        Assert.Equal(5, step.CurrentSubStep);

        // Re-enter from back
        step.OnEnter(Context, NavigationDirection.Back);
        Assert.Equal(5, step.CurrentSubStep);
    }

    [Fact]
    public void OnLeave_SetsAnyChatServicesEnabled_Additive()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;
        step.OnEnter(Context, NavigationDirection.Forward);

        // Another channel already enabled
        Context.AnyChatServicesEnabled = true;
        step.SlackEnabled = false;
        step.OnLeave();

        // Should stay true (additive)
        Assert.True(Context.AnyChatServicesEnabled);
    }

    [Fact]
    public void OnLeave_PopulatesChannelEntries_WhenEnabled()
    {
        Context.SelectedPosture = DeploymentPosture.Team;
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;
        step.AllowDirectMessages = true;
        step.ChannelNamesInput = "general, dev";
        step.OnEnter(Context, NavigationDirection.Forward);

        step.OnLeave();

        Assert.True(Context.ChannelEntries.ContainsKey(ChannelType.Slack));
        var entries = Context.ChannelEntries[ChannelType.Slack];
        Assert.Equal(3, entries.Count); // DMs + #general + #dev
        Assert.True(entries[0].IsDmRow);
        Assert.Equal("#general", entries[1].DisplayName);
    }

    [Fact]
    public void OnLeave_RemovesChannelEntries_WhenDisabled()
    {
        Context.ChannelEntries[ChannelType.Slack] = [new ChannelEntry("DMs", "dm", TrustAudience.Personal, true)];
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = false;
        step.OnEnter(Context, NavigationDirection.Forward);

        step.OnLeave();

        Assert.False(Context.ChannelEntries.ContainsKey(ChannelType.Slack));
    }

    [Fact]
    public void ContributeConfig_Disabled_NoSlackSection()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = false;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Slack);
    }

    [Fact]
    public void ContributeConfig_Enabled_SetsSlackSection()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;
        step.AllowDirectMessages = true;
        step.AllowedUserIdsInput = "U123, U456";

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Slack);
        Assert.True(builder.Slack!.Enabled);
        Assert.True(builder.Slack.AllowDirectMessages);
        Assert.Equal(2, builder.Slack.AllowedUserIds!.Count);
    }

    [Fact]
    public void ContributeConfig_UsesResolvedChannelIds_ForChannelAudienceOverrides()
    {
        using var step = new SlackStepViewModel(_fakeProbe)
        {
            SlackEnabled = true,
            ChannelNamesInput = "netclaw-supervisor",
            LastChannelResolution = new SlackChannelResolutionResult(
                true,
                null,
                [new ResolvedSlackChannel("netclaw-supervisor", "C0B62888XAL")],
                [])
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        var entry = Assert.Single(Context.ChannelEntries[ChannelType.Slack]);
        entry.Audience = TrustAudience.Personal;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Slack);
        Assert.Equal("C0B62888XAL", Assert.Single(builder.Slack!.AllowedChannelIds!));

        var audience = Assert.Single(builder.Slack.ChannelAudiences!);
        Assert.Equal("C0B62888XAL", audience.Key);
        Assert.Equal("personal", audience.Value);
    }

    [Fact]
    public void ContributeSecrets_AddsTokens()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;
        step.BotToken = "xoxb-test-bot-token";
        step.AppToken = "xapp-test-app-token";

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);
        // Verifies no exception — full integration test covers file output
    }

    [Fact]
    public async Task ContributeHealthChecks_Disabled_PassesImmediately()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = false;

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].Passed);
        Assert.Contains("disabled", results[0].Label);
    }

    [Fact]
    public async Task ContributeHealthChecks_MissingBotToken_Fails()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;
        step.BotToken = null;

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Passed);
        Assert.Contains("bot token missing", results[0].Label);
    }

    [Fact]
    public async Task ContributeHealthChecks_ProbeSuccess()
    {
        using var step = new SlackStepViewModel(_fakeProbe);
        step.SlackEnabled = true;
        step.BotToken = "xoxb-test";

        _fakeProbe.NextProbeResult = new SlackProbeResult(true, null, "TestTeam", null);

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].Passed);
        Assert.Contains("TestTeam", results[0].Label);
    }

    [Fact]
    public void ParseChannelNames_ParsesCommaSeparated()
    {
        var names = SlackStepViewModel.ParseChannelNames("#general, dev, #random");
        Assert.Equal(3, names.Count);
        Assert.Equal("general", names[0]);
        Assert.Equal("dev", names[1]);
        Assert.Equal("random", names[2]);
    }

    [Fact]
    public void ParseChannelNames_ReturnsEmpty_ForNull()
    {
        var names = SlackStepViewModel.ParseChannelNames(null);
        Assert.Empty(names);
    }

    // ── Fake ──

    private sealed class FakeSlackProbe : ISlackProbe
    {
        public SlackProbeResult NextProbeResult { get; set; } =
            new(false, "not configured", null, null);

        public SlackChannelResolutionResult NextResolutionResult { get; set; } =
            new(true, null, [], []);

        public Task<SlackProbeResult> ProbeAsync(string botToken, CancellationToken ct = default)
            => Task.FromResult(NextProbeResult);

        public Task<SlackChannelResolutionResult> ResolveChannelNamesAsync(
            string botToken, IReadOnlyList<string> channelNames, CancellationToken ct = default)
            => Task.FromResult(NextResolutionResult);
    }
}
