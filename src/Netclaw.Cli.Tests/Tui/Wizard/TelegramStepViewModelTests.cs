// -----------------------------------------------------------------------
// <copyright file="TelegramStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Cli.Telegram;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class TelegramStepViewModelTests : WizardStepTestBase
{
    private readonly FakeTelegramProbe _probe = new();

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 6)]
    [InlineData(true, true, 7)]
    public void SubStepCount_matches_state(bool enabled, bool restrict, int expected)
    {
        using var step = new TelegramStepViewModel(_probe)
        {
            TelegramEnabled = enabled,
            RestrictToSpecificUsers = restrict
        };

        Assert.Equal(expected, step.SubStepCount);
    }

    [Fact]
    public void ContributeConfig_uses_canonical_chat_ids_and_distinct_user_audiences()
    {
        Context.SelectedPosture = DeploymentPosture.Team;
        using var step = new TelegramStepViewModel(_probe)
        {
            TelegramEnabled = true,
            AllowDirectMessages = true,
            MentionOnly = true,
            ChannelIdsInput = "-05364308250",
            AllowedUserIdsInput = "6875639362",
            LastChannelResolution = new TelegramChatResolutionResult(
                true,
                null,
                [new ResolvedTelegramChat("-5364308250", "Netclaw group")],
                [])
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();
        var entries = Context.ChannelEntries[ChannelType.Telegram];
        entries.Single(entry => entry.Id == "dm").Audience = TrustAudience.Public;
        entries.Single(entry => entry.Id == "6875639362").Audience = TrustAudience.Personal;
        entries.Single(entry => !entry.IsDmRow).Audience = TrustAudience.Team;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        var telegram = Assert.IsType<TelegramConfigSection>(builder.Telegram);
        Assert.Equal("-5364308250", Assert.Single(telegram.AllowedChatIds!));
        Assert.Equal("6875639362", Assert.Single(telegram.AllowedUserIds!));
        Assert.True(telegram.AllowDirectMessages);
        Assert.True(telegram.MentionOnly);
        Assert.Equal("public", telegram.ChatAudiences!["dm"]);
        Assert.Equal("personal", telegram.ChatAudiences["6875639362"]);
        Assert.Equal("team", telegram.ChatAudiences["-5364308250"]);
        Assert.DoesNotContain("-05364308250", telegram.ChatAudiences.Keys);
    }

    [Fact]
    public void ContributeConfig_preserves_dm_fallback_without_approved_users()
    {
        Context.SelectedPosture = DeploymentPosture.Public;
        using var step = new TelegramStepViewModel(_probe)
        {
            TelegramEnabled = true,
            AllowDirectMessages = true
        };

        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();
        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Equal("public", builder.Telegram!.ChatAudiences!["dm"]);
    }

    [Fact]
    public async Task Health_check_blocks_an_unresolved_chat_id()
    {
        _probe.NextResolutionResult = new TelegramChatResolutionResult(false, null, [], ["-999"]);
        using var step = new TelegramStepViewModel(_probe)
        {
            TelegramEnabled = true,
            BotToken = "token",
            ChannelIdsInput = "-999"
        };
        var items = new List<HealthCheckItem>();

        await step.ContributeHealthChecksAsync(
            new HealthCheckRunner(items, () => { }), TestContext.Current.CancellationToken);

        Assert.Equal(2, items.Count);
        Assert.False(items[1].Passed);
        Assert.Contains("-999", items[1].Label);
    }
}
