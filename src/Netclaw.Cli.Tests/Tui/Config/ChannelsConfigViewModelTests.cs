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
    public void Add_channel_preserves_credentials_and_writes_channel_audience()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "C09";
        vm.MoveAddChannelAudience(-1); // Team default -> Personal.

        vm.ApplyAddChannel();
        vm.Save();

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["C01", "C02", "C03", "C09"], ToStringArray(channelsRaw));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.ChannelAudiences", out var audiencesRaw));
        var audiences = ToStringDictionary(audiencesRaw);
        Assert.Equal("personal", audiences["C09"]);

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var botToken));
        Assert.Equal("xoxb-test", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));
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
    public void Save_rejects_unresolved_slack_channel_name()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                false,
                null,
                [],
                ["fart"])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "fart";

        vm.ApplyAddChannel();

        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Slack channel not found: #fart", vm.Status.Value.Text);
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
    public void Save_rejects_unresolved_discord_channel_id()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        var discordProbe = new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(
                false,
                null,
                [],
                ["987654321"])
        };
        using var vm = CreateViewModel(discordProbe: discordProbe);
        vm.Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).ChannelIdsInput = "987654321";

        vm.Save();

        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Discord channel ID not found: 987654321", vm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(1, discordProbe.ResolveCallCount);
        Assert.Equal("discord-token", discordProbe.LastBotToken);
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
    public void Save_rejects_unresolved_mattermost_channel_id()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        var mattermostProbe = new FakeMattermostProbe
        {
            NextResolutionResult = new MattermostChannelResolutionResult(
                false,
                null,
                [],
                ["bogus"])
        };
        using var vm = CreateViewModel(mattermostProbe: mattermostProbe);
        vm.Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).ChannelIdsInput = "bogus";

        vm.Save();

        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Mattermost channel ID not found: bogus", vm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(1, mattermostProbe.ResolveCallCount);
        Assert.Equal("https://mattermost.example.com", mattermostProbe.LastServerUrl);
        Assert.Equal("mattermost-token", mattermostProbe.LastBotToken);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    private ChannelsConfigViewModel CreateViewModel(
        FakeSlackProbe? slackProbe = null,
        FakeDiscordProbe? discordProbe = null,
        FakeMattermostProbe? mattermostProbe = null)
        => new(_paths,
            slackProbe ?? new FakeSlackProbe(),
            discordProbe ?? new FakeDiscordProbe(),
            mattermostProbe ?? new FakeMattermostProbe());

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
