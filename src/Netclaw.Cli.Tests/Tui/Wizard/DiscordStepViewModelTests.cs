// -----------------------------------------------------------------------
// <copyright file="DiscordStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Cli.Discord;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class DiscordStepViewModelTests : WizardStepTestBase
{
    private readonly FakeDiscordProbe _fakeProbe = new();

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 5)]
    [InlineData(true, true, 6)]
    public void SubStepCount_MatchesState(bool enabled, bool restrict, int expected)
    {
        using var step = new DiscordStepViewModel(_fakeProbe);
        step.DiscordEnabled = enabled;
        if (restrict) step.RestrictToSpecificUsers = true;
        Assert.Equal(expected, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ThroughAllSubSteps()
    {
        using var step = new DiscordStepViewModel(_fakeProbe);
        step.DiscordEnabled = true;

        Assert.True(step.TryAdvance());
        Assert.Equal(1, step.CurrentSubStep);

        Assert.True(step.TryAdvance());
        Assert.Equal(2, step.CurrentSubStep);

        Assert.True(step.TryAdvance());
        Assert.Equal(3, step.CurrentSubStep);

        Assert.True(step.TryAdvance());
        Assert.Equal(4, step.CurrentSubStep);

        // Sub-step 4 is UserAccessChoice; default RestrictToSpecificUsers=false completes the step
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryAdvance_WithRestrict_AdvancesToAllowedUserIds()
    {
        using var step = new DiscordStepViewModel(_fakeProbe);
        step.DiscordEnabled = true;

        // Advance to sub-step 4 (UserAccessChoice)
        for (var i = 0; i < 4; i++)
            step.TryAdvance();
        Assert.Equal(4, step.CurrentSubStep);

        step.RestrictToSpecificUsers = true;
        Assert.True(step.TryAdvance());
        Assert.Equal(5, step.CurrentSubStep);

        // Sub-step 5 (AllowedUserIds) completes the step
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryAdvance_AllowAnyone_ClearsAllowedUserIds()
    {
        using var step = new DiscordStepViewModel(_fakeProbe);
        step.DiscordEnabled = true;
        step.AllowedUserIdsInput = "129847561203948576";

        // Advance to sub-step 4 (UserAccessChoice)
        for (var i = 0; i < 4; i++)
            step.TryAdvance();

        step.RestrictToSpecificUsers = false;
        Assert.False(step.TryAdvance());
        Assert.Null(step.AllowedUserIdsInput);
    }

    [Fact]
    public void OnLeave_PopulatesChannelEntries_WhenEnabled()
    {
        Context.SelectedPosture = DeploymentPosture.Team;
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            AllowDirectMessages = true,
            ChannelIdsInput = "129847561203948576,130111223344556677"
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        Assert.True(Context.ChannelEntries.ContainsKey(ChannelType.Discord));
        var entries = Context.ChannelEntries[ChannelType.Discord];
        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsDmRow);
        // Team posture, no single allow-listed user → DMs and channels both default to Team.
        Assert.Equal(TrustAudience.Team, entries[0].Audience);
        Assert.Equal("129847561203948576", entries[1].Id);
        Assert.Equal(TrustAudience.Team, entries[1].Audience);
    }

    [Fact]
    public void ContributeConfig_Enabled_SetsDiscordSection()
    {
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            AllowDirectMessages = true,
            ChannelIdsInput = "129847561203948576",
            AllowedUserIdsInput = "130111223344556677",
            // Health check resolves the channel reference to its canonical id; ContributeConfig
            // persists only resolved ids (here id == input, so the assertions are unchanged).
            LastChannelResolution = new DiscordChannelResolutionResult(
                true,
                null,
                [new ResolvedDiscordChannel("129847561203948576", "general", "Test Guild")],
                [])
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Discord);
        Assert.True(builder.Discord!.Enabled);
        Assert.Equal("129847561203948576", builder.Discord.DefaultChannelId);
        Assert.True(builder.Discord.AllowDirectMessages);
        Assert.Equal("130111223344556677", Assert.Single(builder.Discord.AllowedUserIds!));
    }

    [Fact]
    public void ContributeConfig_PersistsResolvedId_NotTypedName_AndOmitsUnresolvedFromAudiences()
    {
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            ChannelIdsInput = "general, ghost-channel",
            // The bot can see "general" → canonical id "129847561203948576"; "ghost-channel" is unresolved.
            LastChannelResolution = new DiscordChannelResolutionResult(
                false,
                null,
                [new ResolvedDiscordChannel("129847561203948576", "general", "Test Guild")],
                ["ghost-channel"])
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        foreach (var entry in Context.ChannelEntries[ChannelType.Discord])
            entry.Audience = TrustAudience.Team;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Discord);
        // The resolved channel persists by its canonical id, never the typed name...
        Assert.Equal("129847561203948576", Assert.Single(builder.Discord!.AllowedChannelIds!));

        var audiences = builder.Discord.ChannelAudiences;
        Assert.NotNull(audiences);
        Assert.True(audiences!.ContainsKey("129847561203948576"));
        // ...and the unresolved channel NAME is NOT written as a dead ACL key the runtime can't match.
        Assert.DoesNotContain("ghost-channel", audiences.Keys);
        Assert.DoesNotContain("general", audiences.Keys);
    }

    [Fact]
    public async Task ContributeHealthChecks_MissingBotToken_Fails()
    {
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = null
        };

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Passed);
        Assert.Contains("bot token missing", results[0].Label);
    }

    [Fact]
    public void ParseChannelIds_ParsesCommaSeparated()
    {
        var ids = DiscordStepViewModel.ParseChannelIds("123, 456, #789");

        Assert.Equal(3, ids.Count);
        Assert.Equal("123", ids[0]);
        Assert.Equal("456", ids[1]);
        Assert.Equal("789", ids[2]);
    }

    [Fact]
    public async Task ContributeHealthChecks_ProbeSuccess_ReportsAuthenticated()
    {
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = "test-token"
        };

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].Passed);
        Assert.Contains("TestBot", results[0].Label);
        Assert.Equal(1, _fakeProbe.ProbeCallCount);
        Assert.Equal("test-token", _fakeProbe.LastBotToken);
    }

    [Fact]
    public async Task ContributeHealthChecks_ProbeFailure_ReportsError()
    {
        _fakeProbe.NextProbeResult = new DiscordProbeResult(false, "Bot token is invalid.", null);

        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = "bad-token"
        };

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Passed);
        Assert.Contains("Bot token is invalid", results[0].Label);
    }

    [Fact]
    public async Task ContributeHealthChecks_ResolvesChannelNames_UpdatesDisplayNames()
    {
        _fakeProbe.NextResolutionResult = new DiscordChannelResolutionResult(
            true, null,
            [new ResolvedDiscordChannel("129847561203948576", "general", "MyServer")],
            []);

        Context.SelectedPosture = DeploymentPosture.Team;
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = "test-token",
            ChannelIdsInput = "129847561203948576"
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.True(results[1].Passed);
        Assert.Contains("resolved (1)", results[1].Label);

        var entries = Context.ChannelEntries[ChannelType.Discord];
        var channelEntry = entries.First(e => !e.IsDmRow);
        Assert.Equal("MyServer / #general", channelEntry.DisplayName);
    }

    [Fact]
    public async Task BackgroundChannelResolution_PublishesResult_AppliedOnLeaveWithoutRace()
    {
        _fakeProbe.NextResolutionResult = new DiscordChannelResolutionResult(
            true, null,
            [new ResolvedDiscordChannel("129847561203948576", "general", "MyServer")],
            []);
        _fakeProbe.ResolveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Context.SelectedPosture = DeploymentPosture.Team;
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = "test-token",
            ChannelIdsInput = "129847561203948576"
        };
        step.OnEnter(Context, NavigationDirection.Forward);

        // Advance 0→1→2→3; the 2→3 transition kicks off the background channel-name prefetch.
        Assert.True(step.TryAdvance());
        Assert.True(step.TryAdvance());
        Assert.True(step.TryAdvance());

        var pending = step.PendingResolution;
        Assert.NotNull(pending);
        Assert.False(pending!.IsCompleted); // gated — still in flight, nothing published yet
        Assert.Null(step.LastChannelResolution);

        // Release the probe and await the tracked task (no Task.Delay/polling).
        _fakeProbe.ResolveGate.SetResult();
        await pending;

        Assert.NotNull(step.LastChannelResolution);
        Assert.True(step.LastChannelResolution!.Success);

        // The loop thread owns ChannelEntries mutation; OnLeave applies the resolved display names.
        step.OnLeave();
        var channelEntry = Context.ChannelEntries[ChannelType.Discord].First(e => !e.IsDmRow);
        Assert.Equal("MyServer / #general", channelEntry.DisplayName);
    }

    [Fact]
    public async Task BackgroundChannelResolution_DisposedBeforeProbeReturns_DropsStaleResult()
    {
        _fakeProbe.NextResolutionResult = new DiscordChannelResolutionResult(
            true, null,
            [new ResolvedDiscordChannel("129847561203948576", "general", "MyServer")],
            []);
        _fakeProbe.ResolveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Context.SelectedPosture = DeploymentPosture.Team;
        var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = "test-token",
            ChannelIdsInput = "129847561203948576"
        };
        step.OnEnter(Context, NavigationDirection.Forward);
        Assert.True(step.TryAdvance());
        Assert.True(step.TryAdvance());
        Assert.True(step.TryAdvance());

        var pending = step.PendingResolution;
        Assert.NotNull(pending);

        // User abandons the step before the probe returns; Dispose cancels the prefetch.
        step.Dispose();

        // Probe completes after cancellation — the token guard must drop the stale result.
        _fakeProbe.ResolveGate.SetResult();
        await pending!;

        Assert.Null(step.LastChannelResolution);
    }

    [Fact]
    public async Task ContributeHealthChecks_PartialResolution_ReportsUnresolved()
    {
        _fakeProbe.NextResolutionResult = new DiscordChannelResolutionResult(
            false, null,
            [new ResolvedDiscordChannel("111111111111111111", "general", "MyServer")],
            ["999999999999999999"]);

        Context.SelectedPosture = DeploymentPosture.Team;
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = "test-token",
            ChannelIdsInput = "111111111111111111,999999999999999999"
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.False(results[1].Passed);
        Assert.Contains("resolved 1/2", results[1].Label);
        Assert.Contains("999999999999999999", results[1].Label);

        var entries = Context.ChannelEntries[ChannelType.Discord];
        var resolvedEntry = entries.First(e => e.Id == "111111111111111111");
        Assert.Equal("MyServer / #general", resolvedEntry.DisplayName);

        var unresolvedEntry = entries.First(e => e.Id == "999999999999999999");
        Assert.Equal("Discord:999999999999999999", unresolvedEntry.DisplayName);
    }
}
