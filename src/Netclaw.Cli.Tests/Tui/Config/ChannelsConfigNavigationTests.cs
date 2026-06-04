// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigNavigationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Cli.Tests.Tui;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Config;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class ChannelsConfigNavigationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ChannelsConfigNavigationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Slack": { "Enabled": true, "AllowedChannelIds": ["C01"] },
              "Discord": { "Enabled": true, "AllowedChannelIds": ["123456789"] },
              "Mattermost": {
                "Enabled": true,
                "ServerUrl": "https://mattermost.example.com",
                "AllowedChannelIds": ["town-square"]
              }
            }
            """);
        File.WriteAllText(_paths.SecretsPath,
            """
            {
              "configVersion": 1,
              "Slack": { "BotToken": "xoxb-existing", "AppToken": "xapp-existing" },
              "Discord": { "BotToken": "discord-existing" },
              "Mattermost": { "BotToken": "mattermost-existing" }
            }
            """);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Channels_Escape_ReturnsToDashboardUsingTerminaHistory()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        dashboardVm.SelectedIndex.Value = dashboardVm.Items
            .Select((item, index) => (item, index))
            .Single(entry => entry.item.Label == "Channels")
            .index;

        dashboardVm.ActivateSelected();
        input.EnqueueKey(ConsoleKey.Escape);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.NotNull(getChannelsVm());
        Assert.Equal("/config", app.CurrentPath);
    }

    [Theory]
    [InlineData(ChannelType.Slack)]
    [InlineData(ChannelType.Discord)]
    [InlineData(ChannelType.Mattermost)]
    public async Task Channels_RotateCredentials_AcceptsTypedCredentialInput(ChannelType channelType)
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);
        MoveToAdapter(input, channelType);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured adapter management.
        MoveToRotateCredentials(input);
        input.EnqueueKey(ConsoleKey.Enter); // Rotate credentials.
        TypeCredentials(input, channelType);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        AssertTypedCredentials(channelsVm, channelType);
        Assert.Equal("Credential changes staged. Press Esc, then d to save.", channelsVm.Status.Value.Text);
    }

    [Theory]
    [InlineData(ChannelType.Slack)]
    [InlineData(ChannelType.Discord)]
    [InlineData(ChannelType.Mattermost)]
    public async Task Channels_FirstTimeAdapterSetup_AcceptsTypedCredentialInput(ChannelType channelType)
    {
        WriteEmptyChannelFiles();
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);
        MoveToAdapter(input, channelType);

        input.EnqueueKey(ConsoleKey.Enter); // Enable selected adapter and enter first-time setup.
        TypeFirstTimeSetup(input, channelType);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, channelsVm.Screen.Value);
        Assert.Equal(channelType, channelsVm.ActiveAdapterType);
        AssertFirstTimeSetup(channelsVm, channelType);
    }

    [Fact]
    public async Task Channels_AddChannel_AcceptsPastedChannelInput()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured Slack management.
        input.EnqueueKey(ConsoleKey.DownArrow); // Add channel.
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueuePaste("#pasted-channel");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.Contains(channelsVm.GetChannelRows(), row => row.Id == "pasted-channel" && !row.IsAddAction);
        Assert.Equal("Added pasted-channel. Press Esc, then d to save.", channelsVm.Status.Value.Text);
    }

    [Fact]
    public async Task Channels_ChannelPermissions_DoesNotRemoveSelectedChannelWithDoneKey()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured Slack management.
        input.EnqueueKey(ConsoleKey.Enter); // Manage channels and permissions.
        input.EnqueueKey(ConsoleKey.D);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.Contains(channelsVm.GetChannelRows(), row => row.Id == "C01" && !row.IsAddAction);
    }

    [Fact]
    public async Task Channels_ChannelPermissions_DeleteRemovesSelectedChannel()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured Slack management.
        input.EnqueueKey(ConsoleKey.Enter); // Manage channels and permissions.
        input.EnqueueKey(ConsoleKey.Delete);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.DoesNotContain(channelsVm.GetChannelRows(), row => row.Id == "C01");
        Assert.Equal("Removed C01. Press Esc, then d to save.", channelsVm.Status.Value.Text);
    }

    [Fact]
    public async Task Channels_FirstTimeSlackBotToken_ShowsValidationError()
    {
        WriteEmptyChannelFiles();
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Enable Slack and enter first-time setup.
        input.EnqueueString("not-a-slack-token");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        var slack = channelsVm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        Assert.Equal(ChannelsEditorValidationMessages.SlackBotTokenPrefix, channelsVm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, channelsVm.Status.Value.Tone);
        Assert.Equal(1, slack.CurrentSubStep);
        Assert.Null(slack.BotToken);
    }

    [Fact]
    public async Task Channels_RotateCredentials_InvalidSlackBotToken_ShowsValidationError()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured Slack management.
        MoveToRotateCredentials(input);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("not-a-slack-token");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        var slack = channelsVm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        Assert.Equal(ChannelsConfigScreen.RotateCredentials, channelsVm.Screen.Value);
        Assert.Equal(ChannelsEditorValidationMessages.SlackBotTokenPrefix, channelsVm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, channelsVm.Status.Value.Tone);
        Assert.Null(slack.BotToken);
    }

    private static void OpenChannels(ConfigDashboardViewModel dashboardVm)
    {
        dashboardVm.SelectedIndex.Value = dashboardVm.Items
            .Select((item, index) => (item, index))
            .Single(entry => entry.item.Label == "Channels")
            .index;
        dashboardVm.ActivateSelected();
    }

    private static void MoveToAdapter(VirtualInputSource input, ChannelType channelType)
    {
        var adapterIndex = channelType switch
        {
            ChannelType.Slack => 0,
            ChannelType.Discord => 1,
            ChannelType.Mattermost => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(channelType), channelType, null)
        };

        for (var i = 0; i < adapterIndex; i++)
            input.EnqueueKey(ConsoleKey.DownArrow);
    }

    private static void MoveToRotateCredentials(VirtualInputSource input)
    {
        for (var i = 0; i < 4; i++)
            input.EnqueueKey(ConsoleKey.DownArrow);
    }

    private static void TypeCredentials(VirtualInputSource input, ChannelType channelType)
    {
        switch (channelType)
        {
            case ChannelType.Slack:
                input.EnqueueString("xoxb-typed-token");
                input.EnqueueKey(ConsoleKey.Tab);
                input.EnqueueString("xapp-typed-token");
                break;
            case ChannelType.Discord:
                input.EnqueueString("discord-typed-token");
                break;
            case ChannelType.Mattermost:
                input.EnqueueKey(ConsoleKey.A, false, false, true);
                input.EnqueueKey(ConsoleKey.Backspace);
                input.EnqueueString("https://typed-mattermost.example.com");
                input.EnqueueKey(ConsoleKey.Tab);
                input.EnqueueString("mattermost-typed-token");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channelType), channelType, null);
        }
    }

    private static void TypeFirstTimeSetup(VirtualInputSource input, ChannelType channelType)
    {
        switch (channelType)
        {
            case ChannelType.Slack:
                input.EnqueueString("xoxb-first-time-token");
                input.EnqueueKey(ConsoleKey.Enter);
                input.EnqueueString("xapp-first-time-token");
                input.EnqueueKey(ConsoleKey.Enter);
                input.EnqueueString("C-first-time");
                input.EnqueueKey(ConsoleKey.Enter);
                SelectSecondOption(input); // Disable DMs.
                SelectSecondOption(input); // Allow anyone in allowed channels.
                break;
            case ChannelType.Discord:
                input.EnqueueString("discord-first-time-token");
                input.EnqueueKey(ConsoleKey.Enter);
                input.EnqueueString("123456789012345678");
                input.EnqueueKey(ConsoleKey.Enter);
                SelectSecondOption(input); // Disable DMs.
                SelectSecondOption(input); // Allow anyone in allowed channels.
                break;
            case ChannelType.Mattermost:
                input.EnqueueString("https://first-time-mattermost.example.com");
                input.EnqueueKey(ConsoleKey.Enter);
                input.EnqueueString("mattermost-first-time-token");
                input.EnqueueKey(ConsoleKey.Enter);
                input.EnqueueString("town-square");
                input.EnqueueKey(ConsoleKey.Enter);
                SelectSecondOption(input); // Disable DMs.
                SelectSecondOption(input); // Allow anyone in allowed channels.
                input.EnqueueKey(ConsoleKey.Enter); // Skip optional callback URL.
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channelType), channelType, null);
        }
    }

    private static void SelectSecondOption(VirtualInputSource input)
    {
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
    }

    private static void AssertTypedCredentials(ChannelsConfigViewModel vm, ChannelType channelType)
    {
        switch (channelType)
        {
            case ChannelType.Slack:
                var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
                Assert.Equal("xoxb-typed-token", slack.BotToken);
                Assert.Equal("xapp-typed-token", slack.AppToken);
                break;
            case ChannelType.Discord:
                var discord = vm.Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
                Assert.Equal("discord-typed-token", discord.BotToken);
                break;
            case ChannelType.Mattermost:
                var mattermost = vm.Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);
                Assert.Equal("https://typed-mattermost.example.com", mattermost.ServerUrl);
                Assert.Equal("mattermost-typed-token", mattermost.BotToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channelType), channelType, null);
        }
    }

    private static void AssertFirstTimeSetup(ChannelsConfigViewModel vm, ChannelType channelType)
    {
        switch (channelType)
        {
            case ChannelType.Slack:
                var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
                Assert.Equal("xoxb-first-time-token", slack.BotToken);
                Assert.Equal("xapp-first-time-token", slack.AppToken);
                Assert.Equal("C-first-time", slack.ChannelNamesInput);
                break;
            case ChannelType.Discord:
                var discord = vm.Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
                Assert.Equal("discord-first-time-token", discord.BotToken);
                Assert.Equal("123456789012345678", discord.ChannelIdsInput);
                break;
            case ChannelType.Mattermost:
                var mattermost = vm.Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);
                Assert.Equal("https://first-time-mattermost.example.com", mattermost.ServerUrl);
                Assert.Equal("mattermost-first-time-token", mattermost.BotToken);
                Assert.Equal("town-square", mattermost.ChannelIdsInput);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channelType), channelType, null);
        }
    }

    private void WriteEmptyChannelFiles()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1
            }
            """);
        File.WriteAllText(_paths.SecretsPath,
            """
            {
              "configVersion": 1
            }
            """);
    }

    private TerminaApplication CreateHeadlessApp(
        out VirtualInputSource input,
        out ConfigDashboardViewModel dashboardVm,
        out Func<ChannelsConfigViewModel?> getChannelsVm)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        var navigationState = new ConfigDashboardNavigationState();
        var tuiNavigation = new TuiNavigation();
        ConfigDashboardViewModel? capturedDashboardVm = null;
        ChannelsConfigViewModel? capturedChannelsVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddSingleton(tuiNavigation);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/config", builder =>
        {
            builder.RegisterRoute<ConfigDashboardPage, ConfigDashboardViewModel>(
                "/config",
                _ => new ConfigDashboardPage(),
                _ =>
                {
                    capturedDashboardVm = new ConfigDashboardViewModel(navigationState);
                    return capturedDashboardVm;
                });
            builder.RegisterRoute<ChannelsConfigPage, ChannelsConfigViewModel>(
                "/channels",
                _ => new ChannelsConfigPage(),
                _ =>
                {
                    capturedChannelsVm = new ChannelsConfigViewModel(
                        _paths,
                        new FakeSlackProbe(),
                        new FakeDiscordProbe(),
                        new FakeMattermostProbe(),
                        tuiNavigation);
                    return capturedChannelsVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();
        tuiNavigation.Attach(app);

        dashboardVm = capturedDashboardVm!;
        getChannelsVm = () => capturedChannelsVm;
        return app;
    }
}
