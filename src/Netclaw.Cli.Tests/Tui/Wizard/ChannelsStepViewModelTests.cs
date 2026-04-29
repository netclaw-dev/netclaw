// -----------------------------------------------------------------------
// <copyright file="ChannelsStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class ChannelsStepViewModelTests : WizardStepTestBase
{

    [Fact]
    public void IsApplicable_True_WhenChatServicesEnabled()
    {
        Context.AnyChatServicesEnabled = true;
        using var step = new ChannelsStepViewModel();
        Assert.True(step.IsApplicable(Context));
    }

    [Fact]
    public void IsApplicable_False_WhenNoChatServices()
    {
        Context.AnyChatServicesEnabled = false;
        using var step = new ChannelsStepViewModel();
        Assert.False(step.IsApplicable(Context));
    }

    [Fact]
    public void AllEntries_FlattensAcrossSources()
    {
        Context.ChannelEntries[ChannelType.Slack] =
        [
            new ChannelEntry("#general", "C123", TrustAudience.Team),
            new ChannelEntry("DMs", "dm", TrustAudience.Personal, isDmRow: true)
        ];
        Context.ChannelEntries[ChannelType.Tui] =
        [
            new ChannelEntry("#dev-chat", "123456", TrustAudience.Team)
        ];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);

        var all = step.AllEntries;
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void AddEntry_AddsToCorrectSourceBucket()
    {
        using var step = new ChannelsStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);

        step.AddEntry(ChannelType.Slack, new ChannelEntry("#random", "random", TrustAudience.Team));

        Assert.Single(Context.ChannelEntries[ChannelType.Slack]);
        Assert.Equal("#random", Context.ChannelEntries[ChannelType.Slack][0].DisplayName);
    }

    [Fact]
    public void RemoveEntry_RemovesFromCorrectBucket()
    {
        var entry = new ChannelEntry("#general", "C123", TrustAudience.Team);
        Context.ChannelEntries[ChannelType.Slack] = [entry];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);

        Assert.True(step.RemoveEntry(entry));
        Assert.Empty(Context.ChannelEntries[ChannelType.Slack]);
    }

    [Fact]
    public void GetSource_ReturnsCorrectSource()
    {
        var slackEntry = new ChannelEntry("#general", "C123", TrustAudience.Team);
        var discordEntry = new ChannelEntry("#dev", "123", TrustAudience.Team);
        Context.ChannelEntries[ChannelType.Slack] = [slackEntry];
        Context.ChannelEntries[ChannelType.Tui] = [discordEntry];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);

        Assert.Equal(ChannelType.Slack, step.GetSource(slackEntry));
        Assert.Equal(ChannelType.Tui, step.GetSource(discordEntry));
    }

    [Fact]
    public void GetPreferredAddSource_ReturnsOnlyConfiguredSource()
    {
        Context.ChannelEntries[ChannelType.Discord] = [new ChannelEntry("Discord DMs", "dm", TrustAudience.Team, true)];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);

        Assert.Equal(ChannelType.Discord, step.GetPreferredAddSource());
    }

    [Fact]
    public void GetPreferredAddSource_PrefersSlack_WhenMultipleSourcesExist()
    {
        Context.ChannelEntries[ChannelType.Discord] = [new ChannelEntry("Discord DMs", "dm", TrustAudience.Team, true)];
        Context.ChannelEntries[ChannelType.Slack] = [new ChannelEntry("DMs", "dm", TrustAudience.Team, true)];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);

        Assert.Equal(ChannelType.Slack, step.GetPreferredAddSource());
    }
}
