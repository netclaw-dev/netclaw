// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Cli.Config;
using Netclaw.Cli.Discord;
using Netclaw.Cli.Mattermost;
using Netclaw.Cli.Tests.Tui;
using Netclaw.Cli.Tui.Config;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class ChannelsConfigViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public static TheoryData<ChannelType, string, string[]> ResetConnectionCases { get; } = new()
    {
        { ChannelType.Slack, "Slack", ["Slack.BotToken", "Slack.AppToken"] },
        { ChannelType.Discord, "Discord", ["Discord.BotToken"] },
        { ChannelType.Mattermost, "Mattermost", ["Mattermost.BotToken"] }
    };

    public static TheoryData<ChannelType> ChannelTypes { get; } = new()
    {
        ChannelType.Slack,
        ChannelType.Discord,
        ChannelType.Mattermost
    };

    public ChannelsConfigViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Channels_editor_hosts_original_channel_picker_adapters()
    {
        using var vm = CreateViewModel();

        var labels = vm.Step.Adapters.Select(static item => item.DisplayName).ToArray();

        Assert.Equal(["Slack", "Discord", "Mattermost"], labels);
    }

    [Fact]
    public void Channels_editor_validator_maps_static_errors_to_fields()
    {
        var model = new ChannelsEditorModel
        {
            Slack =
            {
                Enabled = true,
                BotTokenDraft = "not-a-slack-token",
                HasPersistedAppToken = true,
            }
        };
        var validator = new ChannelsEditorValidationAdapter();

        var result = validator.Validate(model);

        var issue = Assert.Single(result.IssuesFor(ChannelsEditorFieldPaths.SlackBotToken));
        Assert.Equal(ChannelsEditorValidationMessages.SlackBotTokenPrefix, issue.Message);
    }

    [Fact]
    public void Existing_config_prefills_picker_and_adapter_drafts()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();

        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        var mattermost = vm.Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);

        Assert.True(vm.Step.IsAdapterEnabled(ChannelType.Slack));
        Assert.False(vm.Step.IsAdapterEnabled(ChannelType.Discord));
        Assert.True(vm.Step.IsAdapterEnabled(ChannelType.Mattermost));
        Assert.Equal("3 channels, 2 users", vm.Step.GetAdapterSummary(0));
        Assert.Equal("disabled, saved setup", vm.Step.GetAdapterSummary(1));
        Assert.Equal("1 channel", vm.Step.GetAdapterSummary(2));
        Assert.True(slack.HasPersistedBotToken);
        Assert.True(slack.HasPersistedAppToken);
        Assert.Equal("C01, C02, C03", slack.ChannelNamesInput);
        Assert.Equal("U01, U02", slack.AllowedUserIdsInput);
        Assert.Equal("https://mattermost.example.com", mattermost.ServerUrl);
        Assert.True(mattermost.HasPersistedBotToken);
    }

    [Fact]
    public void Save_preserves_blank_existing_secrets_and_updates_config()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        slack.ChannelNamesInput = "C09";
        slack.AllowedUserIdsInput = "U09";

        vm.Save();

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["C09"], ToStringArray(channelsRaw));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.DefaultChannelId", out var defaultChannel));
        Assert.Equal("C09", defaultChannel);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedUserIds", out var usersRaw));
        Assert.Equal(["U09"], ToStringArray(usersRaw));

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var botToken));
        Assert.Equal("xoxb-test", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.AppToken", out var appToken));
        Assert.Equal("xapp-test", ConfigFileHelper.DecryptIfEncrypted(_paths, appToken?.ToString()));
    }

    [Fact]
    public void Save_sets_new_secret_without_serializing_plaintext()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1
            }
            """);
        using var vm = CreateViewModel();
        vm.Step.LoadAdapterState(ChannelType.Discord, enabled: true, summary: "1 channel", adapter =>
        {
            var discord = (DiscordStepViewModel)adapter;
            discord.DiscordEnabled = true;
            discord.BotToken = "new-discord-token";
            discord.ChannelIdsInput = "123456789";
        });

        vm.Save();

        var serializedSecrets = File.ReadAllText(_paths.SecretsPath);
        Assert.DoesNotContain("new-discord-token", serializedSecrets, StringComparison.Ordinal);
        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Discord.BotToken", out var token));
        Assert.Equal("new-discord-token", ConfigFileHelper.DecryptIfEncrypted(_paths, token?.ToString()));
    }

    [Fact]
    public void Save_disabled_existing_provider_preserves_dormant_fields_and_secrets()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();

        vm.Step.ToggleAdapter(0);
        vm.Save();

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.Enabled", out var enabled));
        Assert.False(Assert.IsType<bool>(enabled));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(3, ToStringArray(channelsRaw).Length);

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var botToken));
        Assert.Equal("xoxb-test", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));
    }

    [Fact]
    public void Save_blocks_enabled_provider_with_missing_required_secret()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1
            }
            """);
        using var vm = CreateViewModel();
        vm.Step.LoadAdapterState(ChannelType.Slack, enabled: true, summary: "configured", adapter =>
        {
            var slack = (SlackStepViewModel)adapter;
            slack.SlackEnabled = true;
            slack.AppToken = "xapp-test";
        });

        vm.Save();

        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Slack bot token is required.", vm.Status.Value.Text);
    }

    [Fact]
    public void Save_blocks_invalid_slack_token_before_probe()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var slackProbe = new FakeSlackProbe();
        using var vm = CreateViewModel(slackProbe: slackProbe);
        vm.Step.LoadAdapterState(ChannelType.Slack, enabled: true, summary: "configured", adapter =>
        {
            var slack = (SlackStepViewModel)adapter;
            slack.SlackEnabled = true;
            slack.BotToken = "not-a-slack-token";
            slack.AppToken = "xapp-test";
            slack.ChannelNamesInput = "netclaw-support";
        });

        vm.Save();

        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Slack bot token must start with xoxb-.", vm.Status.Value.Text);
        Assert.Equal(0, slackProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));
    }

    [Fact]
    public void Save_blocks_invalid_mattermost_url_before_probe()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var mattermostProbe = new FakeMattermostProbe();
        using var vm = CreateViewModel(mattermostProbe: mattermostProbe);
        vm.Step.LoadAdapterState(ChannelType.Mattermost, enabled: true, summary: "configured", adapter =>
        {
            var mattermost = (MattermostStepViewModel)adapter;
            mattermost.MattermostEnabled = true;
            mattermost.ServerUrl = "not-a-url";
            mattermost.BotToken = "mattermost-token";
            mattermost.ChannelIdsInput = "town-square";
        });

        vm.Save();

        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Mattermost server URL must be an absolute http:// or https:// URL.", vm.Status.Value.Text);
        Assert.Equal(0, mattermostProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));
    }

    [Fact]
    public void Back_from_saved_picker_returns_to_dashboard_or_quits()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.Save();

        vm.GoBack();

        Assert.True(vm.IsSaved.Value);
        Assert.True(vm.ShutdownRequestedForTest);
    }

    [Fact]
    public void Config_picker_exposes_done_row_without_save_action()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();

        Assert.True(vm.Step.ShowDonePickerRow);
        Assert.False(vm.Step.ShowDoneAction);
        Assert.Equal("Done adding channels", vm.Step.DonePickerRowLabel);
        Assert.Equal(vm.Step.Adapters.Count + 1, vm.Step.PickerRowCount);
    }

    [Fact]
    public void Channel_permissions_done_row_returns_to_adapter_menu()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.ActivateManagementMenuItem();
        var doneIndex = vm.GetChannelRows()
            .Select((row, index) => (row, index))
            .Single(entry => entry.row.IsDoneAction)
            .index;

        vm.MoveChannelRow(doneIndex);
        vm.OpenSelectedChannelAudience();

        Assert.Equal(ChannelsConfigScreen.AdapterMenu, vm.Screen.Value);
        Assert.Equal("Done adding channels. Completed changes are already saved.", vm.Status.Value.Text);
    }

    [Fact]
    public void Esc_from_incomplete_add_channel_draft_writes_nothing()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "C99";

        vm.GoBack();

        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, vm.Screen.Value);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public void Enable_slack_then_discord_with_channels_then_escape_preserves_both_sections()
    {
        // Reproduces the reported data-loss: a fresh config, enable Slack + add a
        // channel through the picker sub-flow, then enable Discord + add a channel,
        // then Escape back to the dashboard. Both provider sections must survive.
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1
            }
            """);
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                true,
                null,
                [new ResolvedSlackChannel("general", "C100")],
                [])
        };
        var discordProbe = new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(
                true,
                null,
                [new ResolvedDiscordChannel("555000111", "ops", "Guild")],
                [])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe, discordProbe: discordProbe);

        EnableAdapterFromPickerWithChannel(vm, ChannelType.Slack, botToken: "xoxb-test", appToken: "xapp-test", channelInput: "general");

        // After Slack setup + add channel the config on disk must already carry Slack.
        var afterSlack = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(afterSlack, "Slack.Enabled", out var slackEnabledEarly));
        Assert.True(Assert.IsType<bool>(slackEnabledEarly));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterSlack, "Slack.AllowedChannelIds", out var slackChannelsEarly));
        Assert.Equal(["C100"], ToStringArray(slackChannelsEarly));

        EnableAdapterFromPickerWithChannel(vm, ChannelType.Discord, botToken: "discord-token", appToken: null, channelInput: "555000111");

        // After Discord setup both sections must be present on disk.
        var afterDiscord = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(afterDiscord, "Slack.Enabled", out var slackEnabledMid));
        Assert.True(Assert.IsType<bool>(slackEnabledMid));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterDiscord, "Slack.AllowedChannelIds", out var slackChannelsMid));
        Assert.Equal(["C100"], ToStringArray(slackChannelsMid));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterDiscord, "Discord.Enabled", out var discordEnabledMid));
        Assert.True(Assert.IsType<bool>(discordEnabledMid));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterDiscord, "Discord.AllowedChannelIds", out var discordChannelsMid));
        Assert.Equal(["555000111"], ToStringArray(discordChannelsMid));

        // Escape from the picker back to the dashboard.
        vm.GoBack();

        var afterEscape = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(afterEscape, "Slack.Enabled", out var slackEnabledFinal));
        Assert.True(Assert.IsType<bool>(slackEnabledFinal));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterEscape, "Slack.AllowedChannelIds", out var slackChannelsFinal));
        Assert.Equal(["C100"], ToStringArray(slackChannelsFinal));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterEscape, "Discord.Enabled", out var discordEnabledFinal));
        Assert.True(Assert.IsType<bool>(discordEnabledFinal));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterEscape, "Discord.AllowedChannelIds", out var discordChannelsFinal));
        Assert.Equal(["555000111"], ToStringArray(discordChannelsFinal));
    }

    [Fact]
    public void Enable_slack_then_discord_via_subflow_channel_names_then_escape_preserves_both()
    {
        // Variant that mirrors the realistic wizard path: channel names are entered
        // during the adapter sub-flow (Slack sub-step 3 / Discord channel-IDs sub-step),
        // which get resolved on the completion autosave. Then add a second adapter and
        // escape. Both sections must survive.
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1
            }
            """);
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                true,
                null,
                [new ResolvedSlackChannel("general", "C100")],
                [])
        };
        var discordProbe = new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(
                true,
                null,
                [new ResolvedDiscordChannel("555000111", "ops", "Guild")],
                [])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe, discordProbe: discordProbe);

        // Slack: toggle from picker, enter token + channel names in the sub-flow.
        vm.Step.CursorIndex = GetAdapterIndex(vm, ChannelType.Slack);
        Assert.True(vm.TryToggleSelectedAdapterFromPicker());
        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        slack.BotToken = "xoxb-test";
        slack.AppToken = "xapp-test";
        slack.ChannelNamesInput = "general";
        for (var i = 0; i < 10 && vm.Step.IsInSubFlow; i++)
            vm.GoNext();
        vm.GoBack(); // ChannelPermissions -> AdapterMenu
        vm.GoBack(); // AdapterMenu -> Picker

        var afterSlack = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(afterSlack, "Slack.AllowedChannelIds", out var slackChannelsEarly));
        Assert.Equal(["C100"], ToStringArray(slackChannelsEarly));

        // Discord: toggle from picker, enter token + channel IDs in the sub-flow.
        vm.Step.CursorIndex = GetAdapterIndex(vm, ChannelType.Discord);
        Assert.True(vm.TryToggleSelectedAdapterFromPicker());
        var discord = vm.Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
        discord.BotToken = "discord-token";
        discord.ChannelIdsInput = "555000111";
        for (var i = 0; i < 10 && vm.Step.IsInSubFlow; i++)
            vm.GoNext();
        vm.GoBack(); // ChannelPermissions -> AdapterMenu
        vm.GoBack(); // AdapterMenu -> Picker

        // Escape to dashboard.
        vm.GoBack();

        var afterEscape = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(afterEscape, "Slack.Enabled", out var slackEnabledFinal));
        Assert.True(Assert.IsType<bool>(slackEnabledFinal));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterEscape, "Slack.AllowedChannelIds", out var slackChannelsFinal));
        Assert.Equal(["C100"], ToStringArray(slackChannelsFinal));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterEscape, "Discord.Enabled", out var discordEnabledFinal));
        Assert.True(Assert.IsType<bool>(discordEnabledFinal));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterEscape, "Discord.AllowedChannelIds", out var discordChannelsFinal));
        Assert.Equal(["555000111"], ToStringArray(discordChannelsFinal));
    }

    [Fact]
    public void Discord_add_then_slack_disable_then_escape_preserves_provider_config()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Discord);
        vm.BeginAddChannel();
        vm.AddChannelInput = "987654321";

        vm.ApplyAddChannel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        MoveToManagementAction(vm, ChannelsManagementAction.ToggleEnabled);
        vm.ActivateManagementMenuItem();
        vm.GoBack();

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.Enabled", out var slackEnabled));
        Assert.False(Assert.IsType<bool>(slackEnabled));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var slackChannelsRaw));
        Assert.Equal(["C01"], ToStringArray(slackChannelsRaw));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.ChannelAudiences", out var slackAudiencesRaw));
        Assert.Equal("team", ToStringDictionary(slackAudiencesRaw)["C01"]);

        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Discord.Enabled", out var discordEnabled));
        Assert.True(Assert.IsType<bool>(discordEnabled));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Discord.AllowedChannelIds", out var discordChannelsRaw));
        Assert.Equal(["123456789", "987654321"], ToStringArray(discordChannelsRaw));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Discord.ChannelAudiences", out var discordAudiencesRaw));
        var discordAudiences = ToStringDictionary(discordAudiencesRaw);
        Assert.Equal("team", discordAudiences["123456789"]);
        Assert.Equal("team", discordAudiences["987654321"]);

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var slackBotToken));
        Assert.Equal("xoxb-test", ConfigFileHelper.DecryptIfEncrypted(_paths, slackBotToken?.ToString()));
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Discord.BotToken", out var discordBotToken));
        Assert.Equal("discord-token", ConfigFileHelper.DecryptIfEncrypted(_paths, discordBotToken?.ToString()));
    }

    [Fact]
    public void Configured_adapter_opens_management_menu_without_token_subflow()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();

        Assert.True(vm.TryOpenSelectedAdapterManagement());

        Assert.Equal(ChannelsConfigScreen.AdapterMenu, vm.Screen.Value);
        Assert.False(vm.Step.IsInSubFlow);
        Assert.Equal(ChannelType.Slack, vm.ActiveAdapterType);
    }

    [Fact]
    public void First_time_adapter_setup_opens_channel_permissions_before_save()
    {
        using var vm = CreateViewModel();
        vm.Step.ToggleAdapter(0);
        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        slack.BotToken = "xoxb-test";
        slack.AppToken = "xapp-test";
        slack.ChannelNamesInput = "C01";

        for (var i = 0; i < 5; i++)
            vm.GoNext();

        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, vm.Screen.Value);
        Assert.Equal(ChannelType.Slack, vm.ActiveAdapterType);
        Assert.Contains(vm.GetChannelRows(), row => row.Id == "C01" && !row.IsAddAction);
    }

    [Fact]
    public void Add_channel_preserves_credentials_and_adds_at_system_default_audience()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        // Resolve-before-add adds an entered ID directly at the deployment-posture
        // default audience (no audience picker during add).
        vm.AddChannelInput = "C09";

        vm.ApplyAddChannel();
        vm.Save();

        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, vm.Screen.Value);
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["C01", "C02", "C03", "C09"], ToStringArray(channelsRaw));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.ChannelAudiences", out var audiencesRaw));
        var audiences = ToStringDictionary(audiencesRaw);
        // Personal deployment posture -> Team channel default.
        Assert.Equal("team", audiences["C09"]);

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var botToken));
        Assert.Equal("xoxb-test", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));
    }

    [Fact]
    public void Add_channel_resolves_name_to_id_before_adding_and_focuses_the_new_row()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                true,
                null,
                [new ResolvedSlackChannel("netclaw-support", "C09")],
                [])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "netclaw-support";

        vm.ApplyAddChannel();

        // The resolve ran with the bot token, the resolved ID was added, and we
        // advanced to the channel list with the new row focused.
        Assert.Equal(1, slackProbe.ResolveCallCount);
        Assert.Equal(["netclaw-support"], slackProbe.LastResolvedNames);
        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, vm.Screen.Value);
        Assert.True(vm.IsSaved.Value);
        var focusedRow = vm.GetChannelRows()[vm.ChannelRowIndex];
        Assert.Equal("C09", focusedRow.Id);
    }

    [Fact]
    public void Add_channel_that_does_not_resolve_is_not_added_and_keeps_the_add_screen()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(false, null, [], ["ghost"])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "ghost";

        vm.ApplyAddChannel();

        Assert.Equal(1, slackProbe.ResolveCallCount);
        Assert.Equal(ChannelsConfigScreen.AddChannel, vm.Screen.Value);
        Assert.Equal("Slack channel not found: #ghost", vm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        // The channel was never added to the in-memory list nor persisted.
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["C01", "C02", "C03"], ToStringArray(channelsRaw));
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Edit_channel_audience_writes_channel_audiences()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);

        vm.OpenSelectedChannelAudience();
        vm.MoveAudienceSelection(1); // C01 Team -> Public.
        vm.ApplyAudienceSelection();
        vm.Save();

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.ChannelAudiences", out var audiencesRaw));
        Assert.Equal("public", ToStringDictionary(audiencesRaw)["C01"]);
    }

    [Fact]
    public void Direct_message_audience_is_saved_without_touching_channels()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginDirectMessages();
        vm.ChangeDirectMessageAudience(1); // Personal -> Team.

        vm.ApplyDirectMessages();
        vm.Save();

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowDirectMessages", out var allowDm));
        Assert.True(Assert.IsType<bool>(allowDm));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.ChannelAudiences", out var audiencesRaw));
        Assert.Equal("team", ToStringDictionary(audiencesRaw)["dm"]);
    }

    [Fact]
    public void Rotate_credentials_preserves_blank_secret_and_updates_nonblank_secret()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginRotateCredentials();
        vm.BotTokenInput = "xoxb-new";
        vm.AppTokenInput = string.Empty;

        vm.ApplyCredentials();
        vm.Save();

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var botToken));
        Assert.Equal("xoxb-new", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.AppToken", out var appToken));
        Assert.Equal("xapp-test", ConfigFileHelper.DecryptIfEncrypted(_paths, appToken?.ToString()));
    }

    [Theory]
    [MemberData(nameof(ResetConnectionCases))]
    public void Reset_connection_deletes_config_section_and_secrets_immediately(
        ChannelType type,
        string configSection,
        string[] secretPaths)
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        using var vm = CreateViewModel();

        ConfirmReset(vm, type);

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.False(ConfigFileHelper.TryGetPathValue(config, configSection, out _));
        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        foreach (var secretPath in secretPaths)
            Assert.False(ConfigFileHelper.TryGetPathValue(secrets, secretPath, out _));
        Assert.True(vm.IsSaved.Value);
        Assert.Equal($"{type} reset saved.", vm.Status.Value.Text);
    }

    [Theory]
    [MemberData(nameof(ChannelTypes))]
    public void Reset_connection_survives_reopening_channels_editor_without_outer_save(
        ChannelType type)
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        using (var vm = CreateViewModel())
        {
            ConfirmReset(vm, type);
        }

        using var reopened = CreateViewModel();

        Assert.False(reopened.Step.IsAdapterKnown(type));
        Assert.False(reopened.Step.IsAdapterEnabled(type));
        Assert.Null(reopened.Step.GetAdapterSummary(GetAdapterIndex(reopened, type)));
    }

    [Theory]
    [InlineData(ChannelType.Discord, "Discord.AllowedChannelIds", "Discord.ChannelAudiences", "987654321")]
    [InlineData(ChannelType.Mattermost, "Mattermost.AllowedChannelIds", "Mattermost.ChannelAudiences", "town-square-2")]
    public void Add_channel_management_is_generic_for_discord_and_mattermost(
        ChannelType type,
        string channelsPath,
        string audiencesPath,
        string newChannelId)
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(type);
        vm.BeginAddChannel();
        vm.AddChannelInput = newChannelId;

        vm.ApplyAddChannel();
        vm.Save();

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, channelsPath, out var channelsRaw));
        Assert.Contains(newChannelId, ToStringArray(channelsRaw));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, audiencesPath, out var audiencesRaw));
        Assert.Equal("team", ToStringDictionary(audiencesRaw)[newChannelId]);
    }

    [Fact]
    public void Save_resolves_slack_channel_names_to_ids_and_remaps_audiences()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                true,
                null,
                [new ResolvedSlackChannel("netclaw-support", "C09")],
                [])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "netclaw-support";

        vm.ApplyAddChannel();
        vm.Save();

        Assert.Equal(1, slackProbe.ResolveCallCount);
        Assert.Equal("xoxb-test", slackProbe.LastBotToken);
        Assert.Equal(["netclaw-support"], slackProbe.LastResolvedNames);
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["C01", "C02", "C03", "C09"], ToStringArray(channelsRaw));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.ChannelAudiences", out var audiencesRaw));
        var audiences = ToStringDictionary(audiencesRaw);
        Assert.Equal("team", audiences["C09"]);
        Assert.DoesNotContain("netclaw-support", audiences.Keys);

        vm.OpenAdapterManagement(ChannelType.Slack);
        var row = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "C09");
        Assert.Equal("#netclaw-support", row.DisplayName);
    }

    [Fact]
    public void Save_persists_and_flags_unresolved_slack_channel_name()
    {
        // The probe's API call worked (ErrorMessage is null) but Success is false because
        // one name did not resolve — the real SlackProbe sets Success = (every name
        // resolved), so any unresolved name makes Success false. The whole adapter must
        // still persist (token + resolved channels + the unresolved name kept as-is),
        // Save() returns true, and the status is a non-blocking warning.
        WriteChannelConfig();
        WriteChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                false,
                null,
                [new ResolvedSlackChannel("openclaw", "C99")],
                ["fake-channel"])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        slack.ChannelNamesInput = "openclaw, fake-channel";

        var saved = vm.Save();

        Assert.True(saved);
        Assert.True(vm.IsSaved.Value);
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("#fake-channel", vm.Status.Value.Text);
        Assert.Equal(1, slackProbe.ResolveCallCount);

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        // Resolved name mapped to its ID; the unresolved name kept verbatim.
        Assert.Equal(["C99", "fake-channel"], ToStringArray(channelsRaw));

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var botToken));
        Assert.Equal("xoxb-test", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));

        // The unresolved row is flagged for the red-flag renderer.
        var unresolvedRow = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "fake-channel");
        Assert.True(unresolvedRow.IsUnresolved);
        var resolvedRow = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "C99");
        Assert.False(resolvedRow.IsUnresolved);
    }

    [Fact]
    public void Save_blocks_when_slack_probe_fails_and_persists_nothing()
    {
        // The probe itself failed (ErrorMessage set): we cannot validate, so the save
        // must block and persist nothing — not even the resolved channels or token.
        WriteChannelConfig();
        WriteChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                false,
                "invalid_auth",
                [],
                [])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        slack.ChannelNamesInput = "openclaw";

        var saved = vm.Save();

        Assert.False(saved);
        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Slack channel lookup failed: invalid_auth", vm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(1, slackProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public async Task SaveAsync_surfaces_dynamic_validation_exception_to_awaited_caller()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            ResolutionException = new InvalidOperationException("Slack lookup exploded")
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        slack.ChannelNamesInput = "netclaw-support";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.SaveAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Slack lookup exploded", ex.Message);
        Assert.Equal(1, slackProbe.ResolveCallCount);
    }

    [Fact]
    public async Task Save_from_input_surfaces_dynamic_validation_exception_as_status_without_persistence()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        var slackProbe = new FakeSlackProbe
        {
            ResolutionException = new InvalidOperationException("Slack lookup exploded")
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        slack.ChannelNamesInput = "netclaw-support";

        await vm.SaveFromInputAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Channel settings save failed: Slack lookup exploded", vm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public void Save_persists_and_flags_unresolved_discord_channel_id()
    {
        // The probe's API call worked (ErrorMessage is null) but Success is false because
        // one id did not resolve — the real DiscordProbe sets Success = (every id resolved).
        // The whole Discord adapter persists (token + resolved + unresolved id kept), Save()
        // returns true, status is warning.
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var discordProbe = new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(
                false,
                null,
                [new ResolvedDiscordChannel("123456789", "ops", "Stannard Labs")],
                ["987654321"])
        };
        using var vm = CreateViewModel(discordProbe: discordProbe);
        vm.Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).ChannelIdsInput = "123456789, 987654321";

        var saved = vm.Save();

        Assert.True(saved);
        Assert.True(vm.IsSaved.Value);
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("#987654321", vm.Status.Value.Text);
        Assert.Equal(1, discordProbe.ResolveCallCount);
        Assert.Equal("discord-token", discordProbe.LastBotToken);

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Discord.Enabled", out var enabled));
        Assert.True(Assert.IsType<bool>(enabled));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Discord.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["123456789", "987654321"], ToStringArray(channelsRaw));

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Discord.BotToken", out var botToken));
        Assert.Equal("discord-token", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));

        // Switch the editor to Discord so GetChannelRows reads the Discord resolution.
        vm.OpenAdapterManagement(ChannelType.Discord);
        var unresolvedRow = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "987654321");
        Assert.True(unresolvedRow.IsUnresolved);
    }

    [Fact]
    public void Save_blocks_when_discord_probe_fails_and_persists_nothing()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        var discordProbe = new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(
                false,
                "Unauthorized",
                [],
                [])
        };
        using var vm = CreateViewModel(discordProbe: discordProbe);
        vm.Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).ChannelIdsInput = "987654321";

        var saved = vm.Save();

        Assert.False(saved);
        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Discord channel lookup failed: Unauthorized", vm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(1, discordProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public void Save_uses_resolved_discord_channel_names_in_management_rows()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var discordProbe = new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(
                true,
                null,
                [new ResolvedDiscordChannel("123456789", "netclaw", "Stannard Labs")],
                [])
        };
        using var vm = CreateViewModel(discordProbe: discordProbe);

        vm.Save();
        vm.OpenAdapterManagement(ChannelType.Discord);

        var row = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "123456789");
        Assert.Equal("Stannard Labs / #netclaw", row.DisplayName);
    }

    [Fact]
    public void Open_management_resolves_persisted_slack_channel_labels()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                true,
                null,
                [new ResolvedSlackChannel("general", "C01")],
                [])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);

        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.ActivateManagementMenuItem();

        Assert.Equal(1, slackProbe.ResolveCallCount);
        Assert.Equal(["C01"], slackProbe.LastResolvedNames);
        var row = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "C01");
        Assert.Equal("#general", row.DisplayName);
    }

    [Fact]
    public async Task SaveAsync_cancels_and_awaits_in_flight_label_refresh_before_writing()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            // Block the resolve so the background refresh is genuinely in flight when we save.
            DelayBeforeResult = TimeSpan.FromMinutes(5),
            NextResolutionResult = new SlackChannelResolutionResult(
                true, null, [new ResolvedSlackChannel("general", "C01")], []),
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);

        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.ActivateManagementMenuItem(); // starts the background label refresh — it blocks in the probe

        Assert.Equal(1, slackProbe.ResolveCallCount);
        Assert.False(vm.PendingLabelRefresh?.IsCompleted ?? true); // background is in flight

        var saved = await vm.SaveAsync(TestContext.Current.CancellationToken);

        // The save cancelled and awaited the blocked background refresh rather than racing its disk
        // write or hanging for the 5-minute probe delay; the tracked task is unwound to null.
        Assert.True(saved);
        Assert.Null(vm.PendingLabelRefresh);
    }

    [Fact]
    public void Open_management_normalizes_resolved_slack_channel_name_to_id_and_persists()
    {
        // Bug C: a channel saved as a literal NAME (it did not resolve at first save) stays inert
        // in the runtime ACL, which matches AllowedChannelIds by Slack channel ID. Once the bot can
        // see the channel, re-opening management must rewrite the stored name to its canonical ID
        // and persist so the ACL matches — and the audience must travel with it.
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Slack": {
                "Enabled": true,
                "SocketMode": true,
                "AllowedChannelIds": ["C01", "netclaw-test"],
                "AllowedUserIds": ["U01"],
                "AllowDirectMessages": true,
                "ChannelAudiences": { "C01": "team", "netclaw-test": "public", "dm": "personal" }
              }
            }
            """);
        WriteChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                true,
                null,
                [new ResolvedSlackChannel("general", "C01"), new ResolvedSlackChannel("netclaw-test", "C99")],
                [])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);

        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.ActivateManagementMenuItem();

        // The stored name was rewritten to its ID on disk so the runtime ACL can match it.
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["C01", "C99"], ToStringArray(channelsRaw));
        // The audience moved from the name to the ID; the stale name key is gone.
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.ChannelAudiences", out var audiencesRaw));
        var audiences = ToStringDictionary(audiencesRaw);
        Assert.Equal("public", audiences["C99"]);
        Assert.DoesNotContain("netclaw-test", audiences.Keys);
        // The row now renders the resolved label like any other channel.
        var row = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "C99");
        Assert.Equal("#netclaw-test", row.DisplayName);
    }

    [Fact]
    public void Open_management_does_not_rewrite_already_canonical_slack_channels()
    {
        // Guard against spurious writes: opening management when every channel is already stored
        // as its canonical ID must not rewrite the config file at all.
        WriteChannelConfig(); // AllowedChannelIds: ["C01", "C02", "C03"] — all IDs.
        WriteChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                true,
                null,
                [
                    new ResolvedSlackChannel("general", "C01"),
                    new ResolvedSlackChannel("dev", "C02"),
                    new ResolvedSlackChannel("random", "C03")
                ],
                [])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);

        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.ActivateManagementMenuItem();

        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Open_management_resolves_persisted_discord_channel_labels()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var discordProbe = new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(
                true,
                null,
                [new ResolvedDiscordChannel("123456789", "ops", "Stannard Labs")],
                [])
        };
        using var vm = CreateViewModel(discordProbe: discordProbe);

        vm.OpenAdapterManagement(ChannelType.Discord);
        vm.ActivateManagementMenuItem();

        Assert.Equal(1, discordProbe.ResolveCallCount);
        Assert.Equal(["123456789"], discordProbe.LastResolvedIds);
        var row = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "123456789");
        Assert.Equal("Stannard Labs / #ops", row.DisplayName);
    }

    [Fact]
    public void Save_persists_and_flags_unresolved_mattermost_channel_id()
    {
        // The probe's API call worked (ErrorMessage is null) but Success is false because
        // one id did not resolve — the real MattermostProbe sets Success = (every id
        // resolved). The whole Mattermost adapter persists (token + resolved + unresolved
        // id kept), Save() returns true.
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var mattermostProbe = new FakeMattermostProbe
        {
            NextResolutionResult = new MattermostChannelResolutionResult(
                false,
                null,
                [new ResolvedMattermostChannel("town-square", "town-square", "Town Square")],
                ["bogus"])
        };
        using var vm = CreateViewModel(mattermostProbe: mattermostProbe);
        vm.Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).ChannelIdsInput = "town-square, bogus";

        var saved = vm.Save();

        Assert.True(saved);
        Assert.True(vm.IsSaved.Value);
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("#bogus", vm.Status.Value.Text);
        Assert.Equal(1, mattermostProbe.ResolveCallCount);
        Assert.Equal("https://mattermost.example.com", mattermostProbe.LastServerUrl);
        Assert.Equal("mattermost-token", mattermostProbe.LastBotToken);

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Mattermost.Enabled", out var enabled));
        Assert.True(Assert.IsType<bool>(enabled));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Mattermost.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["town-square", "bogus"], ToStringArray(channelsRaw));

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Mattermost.BotToken", out var botToken));
        Assert.Equal("mattermost-token", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));

        // Switch the editor to Mattermost so GetChannelRows reads the Mattermost resolution.
        vm.OpenAdapterManagement(ChannelType.Mattermost);
        var unresolvedRow = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "bogus");
        Assert.True(unresolvedRow.IsUnresolved);
    }

    [Fact]
    public void Save_blocks_when_mattermost_probe_fails_and_persists_nothing()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        var mattermostProbe = new FakeMattermostProbe
        {
            NextResolutionResult = new MattermostChannelResolutionResult(
                false,
                "connection refused",
                [],
                [])
        };
        using var vm = CreateViewModel(mattermostProbe: mattermostProbe);
        vm.Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).ChannelIdsInput = "bogus";

        var saved = vm.Save();

        Assert.False(saved);
        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Mattermost channel lookup failed: connection refused", vm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(1, mattermostProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public void Save_true_for_picker_enabled_adapter_persists_section_even_if_child_flag_desyncs()
    {
        // Regression for the confirmed data-loss: validation gates on the picker's
        // Step.IsAdapterEnabled while the contribution used to gate on the sub-VM's
        // SlackEnabled flag. When those two "is-enabled" sources disagree, the save
        // validated + probed Slack as enabled but persisted only Slack.Enabled=false,
        // dropping the live section while Save() still returned true ("saved").
        //
        // The invariant under test: Save() returning true MUST imply the
        // picker-enabled adapter's section (Enabled=true + AllowedChannelIds) is on
        // disk. Force the desync by flipping only the sub-VM flag — the picker keeps
        // these in lockstep today, so this stands in for any future code path that
        // mutates one source without the other.
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();

        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        Assert.True(vm.Step.IsAdapterEnabled(ChannelType.Slack));
        slack.SlackEnabled = false; // Desync: picker still enabled, child flag disabled.
        slack.ChannelNamesInput = "C01, C02, C03";

        var saved = vm.Save();

        Assert.True(saved);
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.Enabled", out var enabled));
        Assert.True(Assert.IsType<bool>(enabled));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["C01", "C02", "C03"], ToStringArray(channelsRaw));

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var botToken));
        Assert.Equal("xoxb-test", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));
    }

    [Fact]
    public void Save_with_mix_of_resolvable_and_unresolvable_channels_persists_everything()
    {
        // HARD invariant guarding the confirmed data-loss bug: the operator entered
        // three channel NAMES where only one resolves. Before the fix, the unresolved
        // names made ValidateSlackChannelsAsync return an Error, SaveAsync returned
        // false, and NOTHING persisted — not the valid channel, not the bot token. The
        // whole adapter must now persist: Enabled=true, the bot token in secrets.json,
        // the resolved channel mapped to its ID, AND the unresolved names kept as-is.
        WriteChannelConfig();
        WriteChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            // Only "openclaw" is real; the other two are flagged but not blocked. Success
            // is false because not every name resolved (the real probe's semantics), yet
            // the save must still persist everything — that is the invariant under test.
            NextResolutionResult = new SlackChannelResolutionResult(
                false,
                null,
                [new ResolvedSlackChannel("openclaw", "C77")],
                ["netclaw-test", "fake-channel"])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        Assert.True(vm.Step.IsAdapterEnabled(ChannelType.Slack));
        slack.ChannelNamesInput = "netclaw-test, openclaw, fake-channel";

        var saved = vm.Save();

        Assert.True(saved);
        Assert.True(vm.IsSaved.Value);
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("#netclaw-test", vm.Status.Value.Text);
        Assert.Contains("#fake-channel", vm.Status.Value.Text);

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.Enabled", out var enabled));
        Assert.True(Assert.IsType<bool>(enabled));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        // Resolved name -> ID, unresolved names kept verbatim (order preserved).
        Assert.Equal(["netclaw-test", "C77", "fake-channel"], ToStringArray(channelsRaw));

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var botToken));
        Assert.Equal("xoxb-test", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));
    }

    private ChannelsConfigViewModel CreateViewModel(
        FakeSlackProbe? slackProbe = null,
        FakeDiscordProbe? discordProbe = null,
        FakeMattermostProbe? mattermostProbe = null)
        => new(_paths,
            slackProbe ?? new FakeSlackProbe(),
            discordProbe ?? new FakeDiscordProbe(),
            mattermostProbe ?? new FakeMattermostProbe());

    // Drives the real picker-driven entry flow for a brand-new adapter: select its
    // row in the picker, toggle it on (which enters the credential/channel sub-flow),
    // stage credentials + channel input on the step VM, step through the sub-flow to
    // completion (autosaves), then resolve+add one channel in the permissions screen.
    private static void EnableAdapterFromPickerWithChannel(
        ChannelsConfigViewModel vm,
        ChannelType type,
        string botToken,
        string? appToken,
        string channelInput)
    {
        var adapterIndex = GetAdapterIndex(vm, type);
        vm.Step.CursorIndex = adapterIndex;
        Assert.True(vm.TryToggleSelectedAdapterFromPicker());
        Assert.True(vm.Step.IsInSubFlow);

        switch (type)
        {
            case ChannelType.Slack:
                var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
                slack.BotToken = botToken;
                slack.AppToken = appToken;
                break;
            case ChannelType.Discord:
                vm.Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).BotToken = botToken;
                break;
        }

        // Walk the sub-flow to completion; GoNext returns to the picker and opens the
        // channel-permissions screen with an autosave once the sub-flow finishes.
        for (var i = 0; i < 10 && vm.Step.IsInSubFlow; i++)
            vm.GoNext();

        Assert.False(vm.Step.IsInSubFlow);
        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, vm.Screen.Value);

        vm.BeginAddChannel();
        vm.AddChannelInput = channelInput;
        vm.ApplyAddChannel();
        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, vm.Screen.Value);

        // Return to the picker, mirroring "Done adding channels" before switching adapters.
        vm.GoBack();
        vm.GoBack();
        Assert.Equal(ChannelsConfigScreen.Picker, vm.Screen.Value);
    }

    private static void ConfirmReset(ChannelsConfigViewModel vm, ChannelType type)
    {
        vm.OpenAdapterManagement(type);
        var resetIndex = vm.GetManagementMenuItems()
            .Select((item, index) => (item, index))
            .Single(entry => entry.item.Action == ChannelsManagementAction.ResetConnection)
            .index;
        vm.MoveManagementMenu(resetIndex);
        vm.ActivateManagementMenuItem();
        vm.MoveResetConfirmation(1);
        vm.ApplyResetConfirmation();
    }

    private static void MoveToManagementAction(ChannelsConfigViewModel vm, ChannelsManagementAction action)
    {
        var index = vm.GetManagementMenuItems()
            .Select((item, itemIndex) => (item, itemIndex))
            .Single(entry => entry.item.Action == action)
            .itemIndex;

        vm.MoveManagementMenu(index);
    }

    private static int GetAdapterIndex(ChannelsConfigViewModel vm, ChannelType type)
        => vm.Step.Adapters
            .Select((adapter, index) => (adapter.Type, index))
            .Single(entry => entry.Type == type)
            .index;

    private static string[] ToStringArray(object? raw)
        => Assert.IsType<object[]>(raw).Select(static value => value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
            _ => throw new InvalidOperationException("Expected string array value.")
        }).ToArray();

    private static Dictionary<string, string> ToStringDictionary(object? raw)
        => Assert.IsType<Dictionary<string, object>>(raw).ToDictionary(
            static kv => kv.Key,
            static kv => kv.Value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
                _ => throw new InvalidOperationException("Expected string dictionary value.")
            },
            StringComparer.Ordinal);

    private void WriteChannelConfig()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Slack": {
                "Enabled": true,
                "SocketMode": true,
                "AllowedChannelIds": ["C01", "C02", "C03"],
                "AllowedUserIds": ["U01", "U02"],
                "AllowDirectMessages": true,
                "ChannelAudiences": {
                  "C01": "team",
                  "dm": "personal"
                }
              },
              "Discord": {
                "Enabled": false,
                "AllowedChannelIds": ["123"]
              },
              "Mattermost": {
                "Enabled": true,
                "ServerUrl": "https://mattermost.example.com",
                "DefaultChannelId": "town-square"
              }
            }
            """);
    }

    private void WriteChannelSecrets()
    {
        File.WriteAllText(_paths.SecretsPath,
            """
            {
              "configVersion": 1,
              "Slack": {
                "BotToken": "xoxb-test",
                "AppToken": "xapp-test"
              },
              "Mattermost": {
                "BotToken": "mattermost-token"
              }
            }
            """);
    }

    private void WriteAllChannelConfig()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Slack": {
                "Enabled": true,
                "SocketMode": true,
                "AllowedChannelIds": ["C01"],
                "ChannelAudiences": { "C01": "team" }
              },
              "Discord": {
                "Enabled": true,
                "AllowedChannelIds": ["123456789"],
                "ChannelAudiences": { "123456789": "team" }
              },
              "Mattermost": {
                "Enabled": true,
                "ServerUrl": "https://mattermost.example.com",
                "AllowedChannelIds": ["town-square"],
                "ChannelAudiences": { "town-square": "team" }
              }
            }
            """);
    }

    private void WriteAllChannelSecrets()
    {
        File.WriteAllText(_paths.SecretsPath,
            """
            {
              "configVersion": 1,
              "Slack": {
                "BotToken": "xoxb-test",
                "AppToken": "xapp-test"
              },
              "Discord": {
                "BotToken": "discord-token"
              },
              "Mattermost": {
                "BotToken": "mattermost-token"
              }
            }
            """);
    }
}
