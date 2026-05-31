// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Channels;
using Netclaw.Cli.Config;
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
    public void Back_from_saved_returns_to_channel_picker()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.Save();

        vm.GoBack();

        Assert.False(vm.IsSaved.Value);
        Assert.False(vm.ShutdownRequestedForTest);
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

    [Fact]
    public void Reset_connection_deletes_config_section_and_secrets_on_save()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        var resetIndex = vm.GetManagementMenuItems()
            .Select((item, index) => (item, index))
            .Single(entry => entry.item.Action == ChannelsManagementAction.ResetConnection)
            .index;
        vm.MoveManagementMenu(resetIndex);
        vm.ActivateManagementMenuItem();
        vm.MoveResetConfirmation(1);

        vm.ApplyResetConfirmation();
        vm.Save();

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.False(ConfigFileHelper.TryGetPathValue(config, "Slack", out _));
        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.False(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out _));
        Assert.False(ConfigFileHelper.TryGetPathValue(secrets, "Slack.AppToken", out _));
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

    private ChannelsConfigViewModel CreateViewModel()
        => new(_paths, new FakeSlackProbe(), new FakeDiscordProbe());

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
