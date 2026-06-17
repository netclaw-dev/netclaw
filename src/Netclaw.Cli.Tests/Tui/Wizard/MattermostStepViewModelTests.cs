// -----------------------------------------------------------------------
// <copyright file="MattermostStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Cli.Mattermost;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class MattermostStepViewModelTests : WizardStepTestBase
{
    private readonly FakeMattermostProbe _probe = new();

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 7)]
    [InlineData(true, true, 8)]
    public void SubStepCount_MatchesState(bool enabled, bool restrict, int expected)
    {
        using var step = new MattermostStepViewModel(_probe);
        step.MattermostEnabled = enabled;
        if (restrict) step.RestrictToSpecificUsers = true;
        Assert.Equal(expected, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ThroughAllSubSteps_NoRestrict()
    {
        using var step = new MattermostStepViewModel(_probe);
        step.MattermostEnabled = true;

        Assert.True(step.TryAdvance()); // 0 -> 1 server URL
        Assert.Equal(1, step.CurrentSubStep);

        Assert.True(step.TryAdvance()); // 1 -> 2 bot token
        Assert.Equal(2, step.CurrentSubStep);

        Assert.True(step.TryAdvance()); // 2 -> 3 channel IDs
        Assert.Equal(3, step.CurrentSubStep);

        Assert.True(step.TryAdvance()); // 3 -> 4 DM enabled
        Assert.Equal(4, step.CurrentSubStep);

        Assert.True(step.TryAdvance()); // 4 -> 5 user access choice
        Assert.Equal(5, step.CurrentSubStep);

        // RestrictToSpecificUsers default false: 5 -> 7 callback URL (skips allowed user IDs)
        Assert.True(step.TryAdvance());
        Assert.Equal(7, step.CurrentSubStep);

        // Callback URL is the last sub-step — completes the step.
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryAdvance_WithRestrict_AdvancesThroughAllowedUserIds()
    {
        using var step = new MattermostStepViewModel(_probe);
        step.MattermostEnabled = true;

        // Advance to sub-step 5 (user access choice)
        for (var i = 0; i < 5; i++)
            step.TryAdvance();
        Assert.Equal(5, step.CurrentSubStep);

        step.RestrictToSpecificUsers = true;
        Assert.True(step.TryAdvance()); // 5 -> 6 allowed user IDs
        Assert.Equal(6, step.CurrentSubStep);

        Assert.True(step.TryAdvance()); // 6 -> 7 callback URL
        Assert.Equal(7, step.CurrentSubStep);

        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryAdvance_AllowAnyone_ClearsAllowedUserIds()
    {
        using var step = new MattermostStepViewModel(_probe);
        step.MattermostEnabled = true;
        step.AllowedUserIdsInput = "4xp9p3onpins8";

        // Advance to sub-step 5 (user access choice)
        for (var i = 0; i < 5; i++)
            step.TryAdvance();

        step.RestrictToSpecificUsers = false;
        Assert.True(step.TryAdvance());
        Assert.Equal(7, step.CurrentSubStep);
        Assert.Null(step.AllowedUserIdsInput);
    }

    [Fact]
    public void TryGoBack_FromCallbackUrl_SkipsAllowedUserIds_WhenNotRestricting()
    {
        using var step = new MattermostStepViewModel(_probe);
        step.MattermostEnabled = true;

        // Advance to callback URL without restricting
        for (var i = 0; i < 6; i++)
            step.TryAdvance();
        Assert.Equal(7, step.CurrentSubStep);

        // Going back from callback URL should land on user access choice (5), not 6
        Assert.True(step.TryGoBack());
        Assert.Equal(5, step.CurrentSubStep);
    }

    [Fact]
    public void OnLeave_PopulatesChannelEntries_WhenEnabled()
    {
        Context.SelectedPosture = DeploymentPosture.Team;
        using var step = new MattermostStepViewModel(_probe)
        {
            MattermostEnabled = true,
            ServerUrl = "https://mm.example.com",
            AllowDirectMessages = true,
            ChannelIdsInput = "4xp9p3onpins8,9rp7q1abcdef"
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        Assert.True(Context.ChannelEntries.ContainsKey(ChannelType.Mattermost));
        var entries = Context.ChannelEntries[ChannelType.Mattermost];
        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsDmRow);
        // Team posture, no single allow-listed user → DMs and channels both default to Team.
        Assert.Equal(TrustAudience.Team, entries[0].Audience);
        Assert.Equal("4xp9p3onpins8", entries[1].Id);
        Assert.Equal(TrustAudience.Team, entries[1].Audience);
    }

    [Fact]
    public void OnLeave_RemovesChannelEntries_WhenDisabled()
    {
        Context.ChannelEntries[ChannelType.Mattermost] =
            [new ChannelEntry("Mattermost:abc", "abc", TrustAudience.Team)];

        using var step = new MattermostStepViewModel(_probe) { MattermostEnabled = false };
        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        Assert.False(Context.ChannelEntries.ContainsKey(ChannelType.Mattermost));
    }

    [Fact]
    public void ContributeConfig_Enabled_SetsMattermostSection()
    {
        using var step = new MattermostStepViewModel(_probe)
        {
            MattermostEnabled = true,
            ServerUrl = "https://mm.example.com",
            CallbackUrl = "http://netclaw-host:5199/api/mattermost/actions",
            AllowDirectMessages = true,
            ChannelIdsInput = "4xp9p3onpins8",
            AllowedUserIdsInput = "9rp7q1abcdef",
            // Health check resolves the channel reference to its canonical id; ContributeConfig
            // persists only resolved ids (here id == input, so the assertions are unchanged).
            LastChannelResolution = new MattermostChannelResolutionResult(
                true,
                null,
                [new ResolvedMattermostChannel("4xp9p3onpins8", "general", "General")],
                [])
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Mattermost);
        Assert.True(builder.Mattermost!.Enabled);
        Assert.Equal("https://mm.example.com", builder.Mattermost.ServerUrl);
        Assert.Equal("http://netclaw-host:5199/api/mattermost/actions", builder.Mattermost.CallbackUrl);
        Assert.Equal("4xp9p3onpins8", builder.Mattermost.DefaultChannelId);
        Assert.True(builder.Mattermost.AllowDirectMessages);
        Assert.Equal("9rp7q1abcdef", Assert.Single(builder.Mattermost.AllowedUserIds!));
    }

    [Fact]
    public void ContributeConfig_PersistsResolvedId_NotTypedName_AndOmitsUnresolvedFromAudiences()
    {
        using var step = new MattermostStepViewModel(_probe)
        {
            MattermostEnabled = true,
            ServerUrl = "https://mm.example.com",
            ChannelIdsInput = "general, ghost-channel",
            // The bot can see "general" (slug) → canonical id "9rp7q1abcdef"; "ghost-channel" is unresolved.
            LastChannelResolution = new MattermostChannelResolutionResult(
                false,
                null,
                [new ResolvedMattermostChannel("9rp7q1abcdef", "general", "General")],
                ["ghost-channel"])
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        foreach (var entry in Context.ChannelEntries[ChannelType.Mattermost])
            entry.Audience = TrustAudience.Team;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Mattermost);
        // The resolved channel persists by its canonical id, never the typed slug...
        Assert.Equal("9rp7q1abcdef", Assert.Single(builder.Mattermost!.AllowedChannelIds!));

        var audiences = builder.Mattermost.ChannelAudiences;
        Assert.NotNull(audiences);
        Assert.True(audiences!.ContainsKey("9rp7q1abcdef"));
        // ...and the unresolved channel NAME is NOT written as a dead ACL key the runtime can't match.
        Assert.DoesNotContain("ghost-channel", audiences.Keys);
        Assert.DoesNotContain("general", audiences.Keys);
    }

    [Fact]
    public void ContributeConfig_Disabled_DoesNotSetSection()
    {
        using var step = new MattermostStepViewModel(_probe) { MattermostEnabled = false };

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Mattermost);
    }

    [Fact]
    public void ContributeConfig_BlankCallbackUrl_OmitsCallbackUrl()
    {
        using var step = new MattermostStepViewModel(_probe)
        {
            MattermostEnabled = true,
            ServerUrl = "https://mm.example.com",
            CallbackUrl = "   "
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Mattermost);
        Assert.Null(builder.Mattermost!.CallbackUrl);
    }

    [Fact]
    public void ContributeSecrets_Enabled_AddsBotToken()
    {
        using var step = new MattermostStepViewModel(_probe)
        {
            MattermostEnabled = true,
            ServerUrl = "https://mm.example.com",
            BotToken = "mm-bot-token-12345"
        };

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);
        builder.WriteSecretsFile();

        var secretsText = File.ReadAllText(Context.Paths.SecretsPath);
        Assert.Contains("Mattermost", secretsText);
        Assert.Contains("BotToken", secretsText);
    }

    [Fact]
    public void ContributeSecrets_NoBotToken_WritesNothing()
    {
        using var step = new MattermostStepViewModel(_probe)
        {
            MattermostEnabled = true,
            ServerUrl = "https://mm.example.com",
            BotToken = null
        };

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);
        builder.WriteSecretsFile();

        Assert.False(File.Exists(Context.Paths.SecretsPath));
    }

    [Fact]
    public async Task ContributeHealthChecks_Disabled_ReportsDisabled()
    {
        using var step = new MattermostStepViewModel(_probe) { MattermostEnabled = false };

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].Passed);
        Assert.Contains("disabled", results[0].Label);
    }

    [Fact]
    public async Task ContributeHealthChecks_MissingServerUrl_Fails()
    {
        using var step = new MattermostStepViewModel(_probe)
        {
            MattermostEnabled = true,
            ServerUrl = null,
            BotToken = "mm-bot-token"
        };

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Passed);
        Assert.Contains("server URL missing", results[0].Label);
    }

    [Fact]
    public async Task ContributeHealthChecks_MissingBotToken_Fails()
    {
        using var step = new MattermostStepViewModel(_probe)
        {
            MattermostEnabled = true,
            ServerUrl = "https://mm.example.com",
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
    public async Task ContributeHealthChecks_FullyConfigured_Passes()
    {
        using var step = new MattermostStepViewModel(_probe)
        {
            MattermostEnabled = true,
            ServerUrl = "https://mm.example.com",
            BotToken = "mm-bot-token"
        };

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].Passed);
        Assert.Contains("mm.example.com", results[0].Label);
    }

    [Fact]
    public async Task ContributeHealthChecks_WithChannels_ResolvesAndPopulatesResolution()
    {
        _probe.NextResolutionResult = new MattermostChannelResolutionResult(
            true,
            null,
            [new ResolvedMattermostChannel("9rp7q1abcdef", "general", "General")],
            []);

        using var step = new MattermostStepViewModel(_probe)
        {
            MattermostEnabled = true,
            ServerUrl = "https://mm.example.com",
            BotToken = "mm-bot-token",
            ChannelIdsInput = "general"
        };

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Equal(1, _probe.ResolveCallCount);
        Assert.NotNull(step.LastChannelResolution);
        Assert.Equal("9rp7q1abcdef", Assert.Single(step.LastChannelResolution!.Resolved).ChannelId);
        Assert.Contains(results, r => r.Passed == true && r.Label.Contains("channels resolved"));
    }

    [Fact]
    public void ParseChannelIds_ParsesCommaSeparated()
    {
        var ids = MattermostStepViewModel.ParseChannelIds("abc, def, #ghi");

        Assert.Equal(3, ids.Count);
        Assert.Equal("abc", ids[0]);
        Assert.Equal("def", ids[1]);
        Assert.Equal("ghi", ids[2]);
    }
}
