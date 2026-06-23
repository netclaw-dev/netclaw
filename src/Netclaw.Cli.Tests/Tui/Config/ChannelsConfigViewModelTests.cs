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
    public void Constructor_with_unparseable_posture_fails_closed_without_throwing()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            { "configVersion": 1, "Security": { "DeploymentPosture": "NotARealPosture" } }
            """);

        // Before the fix LoadDeploymentPosture threw InvalidOperationException, making the entire
        // Channels page inaccessible on a value the Security page reads without crashing. It now fails
        // closed to Public via the shared DeploymentPostureReader instead of throwing at construction.
        var exception = Record.Exception(() => CreateViewModel().Dispose());

        Assert.Null(exception);
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
    public async Task Save_preserves_blank_existing_secrets_and_updates_config()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        slack.ChannelNamesInput = "C09";
        slack.AllowedUserIdsInput = "U09";

        await vm.SaveAsync(TestContext.Current.CancellationToken);

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
    public async Task Save_sets_new_secret_without_serializing_plaintext()
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

        await vm.SaveAsync(TestContext.Current.CancellationToken);

        var serializedSecrets = File.ReadAllText(_paths.SecretsPath);
        Assert.DoesNotContain("new-discord-token", serializedSecrets, StringComparison.Ordinal);
        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Discord.BotToken", out var token));
        Assert.Equal("new-discord-token", ConfigFileHelper.DecryptIfEncrypted(_paths, token?.ToString()));
    }

    [Fact]
    public async Task Save_disabled_existing_provider_preserves_dormant_fields_and_secrets()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();

        vm.Step.ToggleAdapter(0);
        await vm.SaveAsync(TestContext.Current.CancellationToken);

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
    public async Task Save_blocks_enabled_provider_with_missing_required_secret()
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

        await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Slack bot token is required.", vm.Status.Value.Text);
    }

    [Fact]
    public async Task Save_blocks_invalid_slack_token_before_probe()
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

        await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Slack bot token must start with xoxb-.", vm.Status.Value.Text);
        Assert.Equal(0, slackProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));
    }

    [Fact]
    public async Task Save_blocks_invalid_mattermost_url_before_probe()
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

        await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Mattermost server URL must be an absolute http:// or https:// URL.", vm.Status.Value.Text);
        Assert.Equal(0, mattermostProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));
    }

    [Fact]
    public async Task Back_from_saved_picker_returns_to_dashboard_or_quits()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        await vm.SaveAsync(TestContext.Current.CancellationToken);

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
    public void Adapter_menu_done_row_returns_to_picker()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);

        // The discoverable "Done" row (last management item) backs out to the adapter picker, like Esc.
        var items = vm.GetManagementMenuItems();
        var doneIndex = items
            .Select((item, index) => (item, index))
            .Single(entry => entry.item.Action == ChannelsManagementAction.Done)
            .index;
        Assert.Equal("Done", items[doneIndex].Label);

        vm.MoveManagementMenu(doneIndex);
        vm.ActivateManagementMenuItem();

        Assert.Equal(ChannelsConfigScreen.Picker, vm.Screen.Value);
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
    public async Task Enable_slack_then_discord_with_channels_then_escape_preserves_both_sections()
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

        await EnableAdapterFromPickerWithChannel(vm, ChannelType.Slack, botToken: "xoxb-test", appToken: "xapp-test", channelInput: "general");

        // After Slack setup + add channel the config on disk must already carry Slack.
        var afterSlack = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(afterSlack, "Slack.Enabled", out var slackEnabledEarly));
        Assert.True(Assert.IsType<bool>(slackEnabledEarly));
        Assert.True(ConfigFileHelper.TryGetPathValue(afterSlack, "Slack.AllowedChannelIds", out var slackChannelsEarly));
        Assert.Equal(["C100"], ToStringArray(slackChannelsEarly));

        await EnableAdapterFromPickerWithChannel(vm, ChannelType.Discord, botToken: "discord-token", appToken: null, channelInput: "555000111");

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
    public async Task Enable_slack_then_discord_via_subflow_channel_names_then_escape_preserves_both()
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
        // Sub-flow completion starts a background label refresh that canonicalizes "general" -> "C100".
        // The probe publishes off-loop; drain on the (test = loop) thread, as the page does on render.
        await SettleBackgroundLabelRefreshAsync(vm);
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
        await SettleBackgroundLabelRefreshAsync(vm); // canonicalize the Discord sub-flow channel on the loop thread
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

    // ── Definitive behavior: the persisted allow-list key is the platform's IMMUTABLE channel id.
    // The background resolution (the label-refresh, off the loop thread — never blocking) canonicalizes
    // the editor's channel references to ids: a display name that maps to an id is stored as the id; a
    // display name that maps to NOTHING is never persisted; an id is always kept. This holds for all
    // three adapters. The display name itself is resolved dynamically for rendering and never stored. ──

    [Theory]
    [InlineData(ChannelType.Slack)]
    [InlineData(ChannelType.Discord)]
    [InlineData(ChannelType.Mattermost)]
    public async Task Background_resolution_persists_the_resolved_id_not_the_typed_display_name(ChannelType type)
    {
        WriteFreshConfig();
        using var vm = ViewModelResolving(type, "town-hall", id: "CANONICAL00000000000000000");

        await StageAndRefreshAsync(vm, type, channelInput: "town-hall");

        // The typed display name is gone; the immutable id is what reached disk.
        Assert.Equal(["CANONICAL00000000000000000"], PersistedChannels(type));
    }

    [Theory]
    [InlineData(ChannelType.Slack)]
    [InlineData(ChannelType.Discord)]
    [InlineData(ChannelType.Mattermost)]
    public async Task Background_resolution_does_not_persist_a_display_name_with_no_channel_id(ChannelType type)
    {
        WriteFreshConfig();
        using var vm = ViewModelUnresolved(type, "ghost-channel");

        await StageAndRefreshAsync(vm, type, channelInput: "ghost-channel");

        // A display name the bot can't map to a real channel id is inert in the ACL — it is not saved...
        Assert.Empty(PersistedChannels(type));
        // ...and the operator is told, loudly, exactly what was dropped.
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("ghost-channel", vm.Status.Value.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ChannelType.Slack)]
    [InlineData(ChannelType.Discord)]
    [InlineData(ChannelType.Mattermost)]
    public async Task Background_resolution_does_not_persist_names_when_the_bot_lacks_read_scope(ChannelType type)
    {
        // The exact missing-channels:read case: the probe errors, so nothing maps. A typed display name
        // must not survive as an inert allow-list entry, and the underlying reason is surfaced.
        WriteFreshConfig();
        using var vm = ViewModelProbeError(type, "netclaw-test", "Bot token lacks channels:read scope.");

        await StageAndRefreshAsync(vm, type, channelInput: "netclaw-test");

        Assert.Empty(PersistedChannels(type));
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("channels:read", vm.Status.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Background_resolution_keeps_a_real_id_even_when_the_bot_cannot_enumerate_it()
    {
        // A real channel id is the stable ACL key. If the probe can't currently see it (private channel,
        // bot not yet a member), a transient display-name miss must NOT delete it.
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            { "configVersion": 1, "Slack": { "Enabled": true, "AllowedChannelIds": ["C0B9JCJASP3"] } }
            """);
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(false, null, [], ["C0B9JCJASP3"])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);

        await vm.RefreshChannelLabelsAsync(ChannelType.Slack, TestContext.Current.CancellationToken);

        Assert.Equal(["C0B9JCJASP3"], PersistedChannels(ChannelType.Slack));
    }

    [Fact]
    public void Mattermost_channel_row_shows_the_resolved_display_name_not_the_opaque_id()
    {
        // #1324: the stored ACL key is the opaque Mattermost channel id; the list view must render the
        // resolved human display name (as Slack/Discord already do), not the id.
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            { "configVersion": 1, "Mattermost": { "Enabled": true, "ServerUrl": "https://mm.example.com", "AllowedChannelIds": ["4xp9p3onpins8"] } }
            """);
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Mattermost);
        vm.Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).LastChannelResolution =
            new MattermostChannelResolutionResult(
                true, null, [new ResolvedMattermostChannel("4xp9p3onpins8", "town-square", "Town Square")], []);

        var row = Assert.Single(vm.GetChannelRows(includeAddAction: false), r => r.Id == "4xp9p3onpins8");
        Assert.Equal("Town Square", row.DisplayName);
    }

    [Fact]
    public async Task Add_channel_field_accepts_a_comma_separated_list_and_resolves_each()
    {
        // Regression: "openclaw, netclaw-test" used to be treated as ONE bogus channel. The add field
        // now uses the same CSV parser as the first-connect sub-flow and resolves each reference.
        WriteFreshConfig();
        var slackProbe = new FakeSlackProbe
        {
            ResolveByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["openclaw"] = "C01OPEN",
                ["netclaw-test"] = "C02TEST",
            }
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        vm.Step.LoadAdapterState(ChannelType.Slack, enabled: true, summary: "configured", adapter =>
        {
            var slack = (SlackStepViewModel)adapter;
            slack.SlackEnabled = true;
            slack.BotToken = "xoxb-test";
            slack.AppToken = "xapp-test";
        });
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "openclaw, netclaw-test";

        await vm.ApplyAddChannelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["C01OPEN", "C02TEST"], PersistedChannels(ChannelType.Slack));
    }

    [Fact]
    public async Task Add_channel_field_persists_the_resolved_and_reports_the_unresolved()
    {
        WriteFreshConfig();
        var slackProbe = new FakeSlackProbe
        {
            ResolveByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["openclaw"] = "C01OPEN",
                // "ghost" is intentionally absent — it won't resolve.
            }
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        vm.Step.LoadAdapterState(ChannelType.Slack, enabled: true, summary: "configured", adapter =>
        {
            var slack = (SlackStepViewModel)adapter;
            slack.SlackEnabled = true;
            slack.BotToken = "xoxb-test";
            slack.AppToken = "xapp-test";
        });
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "openclaw, ghost";

        await vm.ApplyAddChannelAsync(TestContext.Current.CancellationToken);

        // The resolvable channel is saved as its id; the unresolvable one is not persisted but is flagged.
        Assert.Equal(["C01OPEN"], PersistedChannels(ChannelType.Slack));
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("ghost", vm.Status.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discord_add_then_slack_disable_then_escape_preserves_provider_config()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Discord);
        vm.BeginAddChannel();
        vm.AddChannelInput = "987654321";

        await vm.ApplyAddChannelAsync(TestContext.Current.CancellationToken);
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
    public async Task Add_channel_preserves_credentials_and_adds_at_system_default_audience()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        // Resolve-before-add adds an entered ID directly at the deployment-posture
        // default audience (no audience picker during add).
        vm.AddChannelInput = "C09";

        await vm.ApplyAddChannelAsync(TestContext.Current.CancellationToken);
        await vm.SaveAsync(TestContext.Current.CancellationToken);

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
    public async Task Add_channel_resolves_name_to_id_before_adding_and_focuses_the_new_row()
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

        await vm.ApplyAddChannelAsync(TestContext.Current.CancellationToken);

        // The resolve ran with the bot token, the resolved ID was added, and we
        // advanced to the channel list with the new row focused.
        Assert.Equal(1, slackProbe.ResolveCallCount);
        Assert.Contains("netclaw-support", slackProbe.LastResolvedNames!);
        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, vm.Screen.Value);
        Assert.True(vm.IsSaved.Value);
        var focusedRow = vm.GetChannelRows()[vm.ChannelRowIndex];
        Assert.Equal("C09", focusedRow.Id);
    }

    [Fact]
    public async Task Add_channel_resolving_to_dm_with_dms_enabled_does_not_throw()
    {
        WriteChannelConfig(); // Slack has AllowDirectMessages: true, so a DM row (Id="dm") exists.
        WriteChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                true,
                null,
                [new ResolvedSlackChannel("dm-collision", "dm")],
                [])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "dm-collision";

        // The resolved id "dm" collides with the DM row's Id; this previously threw from Single().
        await vm.ApplyAddChannelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, vm.Screen.Value);
        // The newly-added channel row (id "dm", NOT the DM row) is focused.
        var focused = vm.GetChannelRows()[vm.ChannelRowIndex];
        Assert.Equal("dm", focused.Id);
        Assert.False(focused.IsDirectMessage);
    }

    [Fact]
    public async Task Add_channel_that_does_not_resolve_is_dropped_with_a_warning()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(false, null, [], ["ghost"])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "ghost";

        await vm.ApplyAddChannelAsync(TestContext.Current.CancellationToken);

        // Unified with the first-connect front door: the typed reference is canonicalized through the
        // shared reconcile. A display name that maps to no channel id is dropped (never persisted) and
        // flagged on the permissions screen — not left inert in the ACL.
        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, vm.Screen.Value);
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("ghost", vm.Status.Value.Text, StringComparison.Ordinal);
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["C01", "C02", "C03"], ToStringArray(channelsRaw));
    }

    [Fact]
    public async Task Edit_channel_audience_writes_channel_audiences()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);

        vm.OpenSelectedChannelAudience();
        vm.MoveAudienceSelection(1); // C01 Team -> Public.
        vm.ApplyAudienceSelection();
        await vm.SaveAsync(TestContext.Current.CancellationToken);

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.ChannelAudiences", out var audiencesRaw));
        Assert.Equal("public", ToStringDictionary(audiencesRaw)["C01"]);
    }

    [Fact]
    public void Cycling_channel_audience_autosaves_without_an_explicit_save()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);

        // The ←/→ audience toggle on the focused channel row (C01, Team). It sets a security-relevant
        // ACL trust tier and must autosave like every other ChannelPermissions mutation — previously
        // it only mutated in-memory state and was silently discarded on Esc.
        var focused = vm.GetChannelRows()[vm.ChannelRowIndex];
        Assert.Equal("C01", focused.Id);

        vm.ChangeSelectedChannelAudience(1); // Team -> Public

        // No explicit Save(): the toggle persisted on its own.
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.ChannelAudiences", out var audiencesRaw));
        Assert.Equal("public", ToStringDictionary(audiencesRaw)["C01"]);
    }

    [Fact]
    public async Task Direct_message_audience_is_saved_without_touching_channels()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginDirectMessages();
        vm.ChangeDirectMessageAudience(1); // Personal -> Team.

        vm.ApplyDirectMessages();
        await vm.SaveAsync(TestContext.Current.CancellationToken);

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowDirectMessages", out var allowDm));
        Assert.True(Assert.IsType<bool>(allowDm));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.ChannelAudiences", out var audiencesRaw));
        Assert.Equal("team", ToStringDictionary(audiencesRaw)["dm"]);
    }

    [Fact]
    public async Task Rotate_credentials_preserves_blank_secret_and_updates_nonblank_secret()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginRotateCredentials();
        vm.BotTokenInput = "xoxb-new";
        vm.AppTokenInput = string.Empty;

        vm.ApplyCredentials();
        await vm.SaveAsync(TestContext.Current.CancellationToken);

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var botToken));
        Assert.Equal("xoxb-new", ConfigFileHelper.DecryptIfEncrypted(_paths, botToken?.ToString()));
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.AppToken", out var appToken));
        Assert.Equal("xapp-test", ConfigFileHelper.DecryptIfEncrypted(_paths, appToken?.ToString()));
    }

    [Theory]
    [MemberData(nameof(ResetConnectionCases))]
    public async Task Reset_connection_deletes_config_section_and_secrets_immediately(
        ChannelType type,
        string configSection,
        string[] secretPaths)
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        using var vm = CreateViewModel();

        await ConfirmReset(vm, type);

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
    public async Task Reset_connection_survives_reopening_channels_editor_without_outer_save(
        ChannelType type)
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        using (var vm = CreateViewModel())
        {
            await ConfirmReset(vm, type);
        }

        using var reopened = CreateViewModel();

        Assert.False(reopened.Step.IsAdapterKnown(type));
        Assert.False(reopened.Step.IsAdapterEnabled(type));
        Assert.Null(reopened.Step.GetAdapterSummary(GetAdapterIndex(reopened, type)));
    }

    [Theory]
    [InlineData(ChannelType.Discord, "Discord.AllowedChannelIds", "Discord.ChannelAudiences", "987654321")]
    [InlineData(ChannelType.Mattermost, "Mattermost.AllowedChannelIds", "Mattermost.ChannelAudiences", "town-square-2")]
    public async Task Add_channel_management_is_generic_for_discord_and_mattermost(
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

        await vm.ApplyAddChannelAsync(TestContext.Current.CancellationToken);
        await vm.SaveAsync(TestContext.Current.CancellationToken);

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, channelsPath, out var channelsRaw));
        Assert.Contains(newChannelId, ToStringArray(channelsRaw));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, audiencesPath, out var audiencesRaw));
        Assert.Equal("team", ToStringDictionary(audiencesRaw)[newChannelId]);
    }

    [Fact]
    public async Task Save_resolves_slack_channel_names_to_ids_and_remaps_audiences()
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

        await vm.ApplyAddChannelAsync(TestContext.Current.CancellationToken);
        await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, slackProbe.ResolveCallCount);
        Assert.Equal("xoxb-test", slackProbe.LastBotToken);
        Assert.Contains("netclaw-support", slackProbe.LastResolvedNames!);
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
    public async Task Save_resolves_discord_channel_name_to_id()
    {
        // The operator entered a display name; the probe resolves it to the channel id, and the id
        // (not the name) is what persists — so the runtime ACL can match it.
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var discordProbe = new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(
                true, null,
                [new ResolvedDiscordChannel("111222333", "ops", "Stannard Labs")],
                [])
        };
        using var vm = CreateViewModel(discordProbe: discordProbe);
        vm.Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).ChannelIdsInput = "ops";

        Assert.True(await vm.SaveAsync(TestContext.Current.CancellationToken));

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Discord.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["111222333"], ToStringArray(channelsRaw));
    }

    [Fact]
    public async Task Save_resolves_mattermost_channel_name_to_id()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var mattermostProbe = new FakeMattermostProbe
        {
            NextResolutionResult = new MattermostChannelResolutionResult(
                true, null,
                [new ResolvedMattermostChannel("ttttttttttttttttttttttttab", "town-square", "Town Square")],
                [])
        };
        using var vm = CreateViewModel(mattermostProbe: mattermostProbe);
        vm.Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).ChannelIdsInput = "town-square";

        Assert.True(await vm.SaveAsync(TestContext.Current.CancellationToken));

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Mattermost.AllowedChannelIds", out var channelsRaw));
        Assert.Equal(["ttttttttttttttttttttttttab"], ToStringArray(channelsRaw));
    }

    [Fact]
    public async Task Save_blocks_when_slack_channel_name_unresolved_and_persists_nothing()
    {
        // The probe's API call worked (ErrorMessage null) but one name did not resolve. Per the
        // fail-loud decision, an unresolvable channel is an inert allow-list entry the runtime ACL
        // can never match, so the save BLOCKS and persists nothing — not even the resolved channel
        // or token — rather than keeping a dead name. The operator must fix or remove it.
        WriteChannelConfig();
        WriteChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
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

        var saved = await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(saved);
        Assert.False(vm.IsSaved.Value);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("#fake-channel", vm.Status.Value.Text);
        Assert.Contains("Could not resolve", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, slackProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public async Task Save_blocks_when_slack_probe_fails_and_persists_nothing()
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

        var saved = await vm.SaveAsync(TestContext.Current.CancellationToken);

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
    public async Task Save_blocks_when_discord_channel_id_unresolved_and_persists_nothing()
    {
        // The probe's API call worked (ErrorMessage null) but one id did not resolve. Per the
        // fail-loud decision the save BLOCKS and persists nothing rather than keeping a dead entry.
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
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

        var saved = await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(saved);
        Assert.False(vm.IsSaved.Value);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("#987654321", vm.Status.Value.Text);
        Assert.Contains("Could not resolve", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, discordProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public async Task Save_blocks_when_discord_probe_fails_and_persists_nothing()
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

        var saved = await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(saved);
        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Discord channel lookup failed: Unauthorized", vm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(1, discordProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public async Task Save_uses_resolved_discord_channel_names_in_management_rows()
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

        await vm.SaveAsync(TestContext.Current.CancellationToken);
        vm.OpenAdapterManagement(ChannelType.Discord);

        var row = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "123456789");
        Assert.Equal("Stannard Labs / #netclaw", row.DisplayName);
    }

    [Fact]
    public async Task Open_management_resolves_persisted_slack_channel_labels()
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
        vm.ActivateManagementMenuItem(); // starts the background label refresh (probe publishes off-loop)
        await SettleBackgroundLabelRefreshAsync(vm); // loop thread applies the published result

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
    public async Task ApplyResetConfirmation_cancels_and_awaits_in_flight_label_refresh_before_writing()
    {
        using var vm = ArrangeSlackResetWithLabelRefreshInFlight();

        await vm.ApplyResetConfirmationAsync(TestContext.Current.CancellationToken);

        // The reset cancelled and awaited the blocked refresh rather than racing its disk write or
        // rebuilding view-model state under it (and without hanging for the 5-minute probe delay);
        // the tracked task is unwound to null.
        Assert.Null(vm.PendingLabelRefresh);
    }

    [Fact]
    public async Task Reset_with_in_flight_label_refresh_completes_under_a_single_worker_synchronization_context()
    {
        // Regression for the macOS CI deadlock. xunit v3 runs tests under a MaxConcurrencySyncContext
        // whose worker pool is sized to the core count. The old reset path bridged async work to the
        // synchronous Termina key handler via .GetAwaiter().GetResult(); on a bounded context that
        // blocks the only free worker while the cancelled probe's continuation is posted back to that
        // same context — a sync-over-async deadlock (it passed on many-core Linux/Windows and hung on
        // macOS's smaller pool). The async migration removes the block. This test pins it
        // deterministically: it drives the whole reset-with-in-flight-refresh scenario on a context
        // with exactly ONE worker, so a reintroduced sync-over-async bridge hangs the worker and trips
        // the watchdog instead of completing.
        using var context = new SingleThreadSynchronizationContext();
        var scenario = context.Run(async () =>
        {
            // Arrange on the single-worker context so the background refresh's continuation captures
            // THIS context — exactly the condition that deadlocked the old blocking reset.
            using var vm = ArrangeSlackResetWithLabelRefreshInFlight();

            // Fire-and-forget exactly like the Termina key handler, then await the serialized write.
            _ = vm.ResetConfirmationFromInputAsync();
            await vm.PendingConfigWrite;

            Assert.Null(vm.PendingLabelRefresh);
        });

        var completed = await Task.WhenAny(
            scenario,
            Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.True(
            ReferenceEquals(completed, scenario),
            "Reset deadlocked under a single-worker SynchronizationContext — a sync-over-async bridge was reintroduced.");
        await scenario; // re-throw any assertion failure raised on the worker thread
    }

    [Fact]
    public async Task Disposing_editor_cancels_and_drains_an_in_flight_config_write()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            // Hold the add's label-resolve open so the config write is genuinely in flight at Dispose.
            DelayBeforeResult = TimeSpan.FromMinutes(5),
            NextResolutionResult = new SlackChannelResolutionResult(
                true, null, [new ResolvedSlackChannel("c-09", "C09")], []),
        };
        var vm = CreateViewModel(slackProbe: slackProbe);
        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.BeginAddChannel();
        vm.AddChannelInput = "c-09";

        // Dispatch the add fire-and-forget exactly like the key handler; it blocks on the 5-minute resolve.
        var write = vm.AddChannelFromInputAsync();
        Assert.False(write.IsCompleted); // in flight, blocked on the label resolve

        // Dispose must cancel the in-flight write via the lifetime token and drain it before returning —
        // not hang for the 5-minute probe, and not let the write resume on a thread-pool continuation and
        // mutate disposed reactive state. Run Dispose off the xunit synchronization context so the
        // in-flight write's continuations can drain on the test context while Dispose waits.
        var dispose = Task.Run(vm.Dispose, TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(
            dispose, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.True(ReferenceEquals(finished, dispose), "Dispose did not drain the cancelled in-flight write promptly.");
        await dispose;  // surface any teardown exception
        await write;    // the cancelled add unwound without surfacing out
    }

    [Fact]
    public async Task Background_label_refresh_probes_off_loop_without_mutating_shared_state_until_marshaled_apply()
    {
        // The off-loop label-refresh race (issue #1426): the background refresh used to RECONCILE in its
        // continuation — mutating _channelAudiences / the Step channel ids / IsSaved / Status / disk — on a
        // thread-pool thread, concurrently with the loop thread reading that state for render (GetChannelRows /
        // FormatChannelLabel) and mutating it in the ←/→ audience handler. _channelAudiences is a plain
        // Dictionary and the audience is a security-relevant ACL trust tier, so the concurrent access was a
        // torn read / lost update.
        //
        // After the fix the background path does ONLY the pure probe off-loop and marshals the reconcile back
        // onto the loop via InvokeAsync. This test proves the invariant deterministically (no Thread.Sleep /
        // Task.Delay — a finite probe-entered/release handshake): hold the probe open mid-flight and assert
        // shared view-model state is UNCHANGED while the continuation is parked off-loop. The stored channel is
        // a literal name ("netclaw-test") that resolves to "C99", so a reconcile WOULD be observable — it must
        // not appear until the marshaled apply runs.
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
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slackProbe = new FakeSlackProbe
        {
            ReleaseResolve = gate.Task, // park the probe in flight until we release it
            NextResolutionResult = new SlackChannelResolutionResult(
                true,
                null,
                [new ResolvedSlackChannel("general", "C01"), new ResolvedSlackChannel("netclaw-test", "C99")],
                [])
        };
        using var vm = CreateViewModel(slackProbe: slackProbe);

        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.ActivateManagementMenuItem();        // starts the fire-and-forget background refresh
        await slackProbe.ResolveEntered;        // the probe is now parked off-loop, continuation suspended

        // While the probe continuation is suspended off-loop, NOTHING shared has moved: render reads succeed and
        // the stored allow-list is still the un-canonicalized name. A regression that reconciled off-loop would
        // already show "C99" here (or tear the Dictionary GetChannelRows enumerates).
        var rowsWhileParked = vm.GetChannelRows(includeAddAction: false)
            .Where(r => !r.IsAction && !r.IsDirectMessage).Select(r => r.Id).ToArray();
        Assert.Equal(["C01", "netclaw-test"], rowsWhileParked);
        Assert.Equal(["C01", "netclaw-test"], PersistedChannels(ChannelType.Slack));
        Assert.False(vm.IsSaved.Value);

        // Release the probe and let the marshaled apply run (InvokeAsync's unbound default applies inline, the
        // post-frame state the loop reaches in production). Only now does the reconcile land.
        gate.SetResult();
        await SettleBackgroundLabelRefreshAsync(vm);

        Assert.Equal(["C01", "C99"], PersistedChannels(ChannelType.Slack));
        Assert.Equal(
            ["C01", "C99"],
            vm.GetChannelRows(includeAddAction: false).Where(r => !r.IsAction && !r.IsDirectMessage).Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task ApplyResetConfirmation_surfaces_save_failure_without_crashing_the_loop()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        using var vm = CreateViewModel();

        vm.OpenAdapterManagement(ChannelType.Slack);
        MoveToManagementAction(vm, ChannelsManagementAction.ResetConnection);
        vm.ActivateManagementMenuItem();
        vm.MoveResetConfirmation(1);

        // Force the reset's session.Save() to fail like a disk-full / permission-denied failure:
        // AtomicFile cannot replace a path that is a directory. Cycle-1's race fix added the
        // cancel-and-await guard here but left the write+reload unguarded.
        File.Delete(_paths.NetclawConfigPath);
        Directory.CreateDirectory(_paths.NetclawConfigPath);

        await vm.ApplyResetConfirmationAsync(TestContext.Current.CancellationToken); // must not throw into the Termina event loop

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        // Stayed on the confirmation screen instead of advancing as if the reset succeeded.
        Assert.Equal(ChannelsConfigScreen.ResetConfirm, vm.Screen.Value);
    }

    [Fact]
    public void Autosave_of_a_completed_action_does_not_run_the_network_channel_probe()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var slackProbe = new FakeSlackProbe();
        using var vm = CreateViewModel(slackProbe: slackProbe);

        vm.OpenAdapterManagement(ChannelType.Slack);
        vm.ActivateManagementMenuItem(); // enters Manage Channels; the background label refresh probes once
        var probesBeforeAutosave = slackProbe.ResolveCallCount;

        // Removing a channel is a completed action that autosaves. With the fix the autosave
        // persists immediately and does NOT block the loop on a fresh channel-access probe.
        vm.RemoveSelectedChannel();

        Assert.True(vm.IsSaved.Value);
        Assert.Equal(probesBeforeAutosave, slackProbe.ResolveCallCount);
    }

    [Fact]
    public async Task Open_management_normalizes_resolved_slack_channel_name_to_id_and_persists()
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
        await SettleBackgroundLabelRefreshAsync(vm); // loop thread applies the published resolution

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
    public async Task Open_management_does_not_rewrite_already_canonical_slack_channels()
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
        await SettleBackgroundLabelRefreshAsync(vm); // reconcile runs but every channel is already canonical

        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Open_management_resolves_persisted_discord_channel_labels()
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
        await SettleBackgroundLabelRefreshAsync(vm); // loop thread applies the published resolution

        Assert.Equal(1, discordProbe.ResolveCallCount);
        Assert.Equal(["123456789"], discordProbe.LastResolvedIds);
        var row = Assert.Single(vm.GetChannelRows(includeAddAction: false), row => row.Id == "123456789");
        Assert.Equal("Stannard Labs / #ops", row.DisplayName);
    }

    [Fact]
    public async Task Save_blocks_when_mattermost_channel_id_unresolved_and_persists_nothing()
    {
        // The probe's API call worked (ErrorMessage null) but one id did not resolve. Per the
        // fail-loud decision the save BLOCKS and persists nothing rather than keeping a dead entry.
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
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

        var saved = await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(saved);
        Assert.False(vm.IsSaved.Value);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("#bogus", vm.Status.Value.Text);
        Assert.Contains("Could not resolve", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, mattermostProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public async Task Save_blocks_when_mattermost_probe_fails_and_persists_nothing()
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

        var saved = await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(saved);
        Assert.False(vm.IsSaved.Value);
        Assert.Equal("Mattermost channel lookup failed: connection refused", vm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(1, mattermostProbe.ResolveCallCount);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public async Task Save_true_for_picker_enabled_adapter_persists_section_even_if_child_flag_desyncs()
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

        var saved = await vm.SaveAsync(TestContext.Current.CancellationToken);

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
    public async Task Save_blocks_when_any_channel_unresolvable_and_persists_nothing()
    {
        // Fail-loud invariant (operator decision): the operator entered three channel NAMES where
        // only one resolves. Rather than persisting the unresolvable names as inert allow-list
        // entries that silently grant nothing (the prior behavior that shipped a dead allow-list and
        // bit a live deployment), the save BLOCKS and persists nothing — not the valid channel, not
        // the bot token — until the bad names are fixed or removed.
        WriteChannelConfig();
        WriteChannelSecrets();
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        var slackProbe = new FakeSlackProbe
        {
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

        var saved = await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(saved);
        Assert.False(vm.IsSaved.Value);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("#netclaw-test", vm.Status.Value.Text);
        Assert.Contains("#fake-channel", vm.Status.Value.Text);
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

    private void WriteFreshConfig()
        => File.WriteAllText(_paths.NetclawConfigPath, """{ "configVersion": 1 }""");

    private string[] PersistedChannels(ChannelType type)
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        return ConfigFileHelper.TryGetPathValue(config, $"{type}.AllowedChannelIds", out var raw)
            ? ToStringArray(raw)
            : [];
    }

    // A VM whose probe resolves `name` -> `id` for the given adapter.
    private ChannelsConfigViewModel ViewModelResolving(ChannelType type, string name, string id) => type switch
    {
        ChannelType.Slack => CreateViewModel(slackProbe: new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(true, null, [new ResolvedSlackChannel(name, id)], [])
        }),
        ChannelType.Discord => CreateViewModel(discordProbe: new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(true, null, [new ResolvedDiscordChannel(id, name, "Guild")], [])
        }),
        ChannelType.Mattermost => CreateViewModel(mattermostProbe: new FakeMattermostProbe
        {
            NextResolutionResult = new MattermostChannelResolutionResult(true, null, [new ResolvedMattermostChannel(id, name, name)], [])
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    // A VM whose probe is reachable but reports `name` as not found (no such channel the bot can see).
    private ChannelsConfigViewModel ViewModelUnresolved(ChannelType type, string name) => type switch
    {
        ChannelType.Slack => CreateViewModel(slackProbe: new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(false, null, [], [name])
        }),
        ChannelType.Discord => CreateViewModel(discordProbe: new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(false, null, [], [name])
        }),
        ChannelType.Mattermost => CreateViewModel(mattermostProbe: new FakeMattermostProbe
        {
            NextResolutionResult = new MattermostChannelResolutionResult(false, null, [], [name])
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    // A VM whose probe fails outright (auth/scope/network) and so maps nothing.
    private ChannelsConfigViewModel ViewModelProbeError(ChannelType type, string name, string error) => type switch
    {
        ChannelType.Slack => CreateViewModel(slackProbe: new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(false, error, [], [name])
        }),
        ChannelType.Discord => CreateViewModel(discordProbe: new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(false, error, [], [name])
        }),
        ChannelType.Mattermost => CreateViewModel(mattermostProbe: new FakeMattermostProbe
        {
            NextResolutionResult = new MattermostChannelResolutionResult(false, error, [], [name])
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    // Stages an enabled adapter carrying `channelInput` in its channel field, then runs the background
    // resolution that canonicalizes it (the path the sub-flow completion triggers, exercised directly).
    private static async Task StageAndRefreshAsync(ChannelsConfigViewModel vm, ChannelType type, string channelInput)
    {
        vm.Step.LoadAdapterState(type, enabled: true, summary: "configured", adapter =>
        {
            switch (adapter)
            {
                case SlackStepViewModel slack:
                    slack.SlackEnabled = true;
                    slack.BotToken = "xoxb-test";
                    slack.ChannelNamesInput = channelInput;
                    break;
                case DiscordStepViewModel discord:
                    discord.DiscordEnabled = true;
                    discord.BotToken = "discord-token";
                    discord.ChannelIdsInput = channelInput;
                    break;
                case MattermostStepViewModel mattermost:
                    mattermost.MattermostEnabled = true;
                    mattermost.ServerUrl = "https://mm.example.com";
                    mattermost.BotToken = "mm-token";
                    mattermost.ChannelIdsInput = channelInput;
                    break;
            }
        });

        await vm.RefreshChannelLabelsAsync(type, TestContext.Current.CancellationToken);
    }

    // Awaits the fire-and-forget background label refresh started by OpenAdapterManagement/ManageChannels
    // (StartChannelLabelResolution). The background path probes off-loop, then marshals the reconcile onto the
    // Termina loop via InvokeAsync. Unbound to a host (as here), InvokeAsync's default runs that apply inline,
    // so by the time the tracked task completes the reconcile has run — exactly the post-frame state the loop
    // reaches in production. Awaiting the task is therefore enough to observe the canonicalized result.
    private static async Task SettleBackgroundLabelRefreshAsync(ChannelsConfigViewModel vm)
    {
        if (vm.PendingLabelRefresh is { } refresh)
            await refresh;
    }

    // Drives the real picker-driven entry flow for a brand-new adapter: select its
    // row in the picker, toggle it on (which enters the credential/channel sub-flow),
    // stage credentials + channel input on the step VM, step through the sub-flow to
    // completion (autosaves), then resolve+add one channel in the permissions screen.
    private static async Task EnableAdapterFromPickerWithChannel(
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

        // Sub-flow completion opens the permissions screen and starts a background label refresh for the
        // names entered in the sub-flow. The probe publishes off-loop; drain it on the (test = loop) thread
        // to canonicalize those names to ids before adding the next channel — what the page does on render.
        await SettleBackgroundLabelRefreshAsync(vm);

        vm.BeginAddChannel();
        vm.AddChannelInput = channelInput;
        await vm.ApplyAddChannelAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, vm.Screen.Value);

        // Return to the picker, mirroring "Done adding channels" before switching adapters.
        vm.GoBack();
        vm.GoBack();
        Assert.Equal(ChannelsConfigScreen.Picker, vm.Screen.Value);
    }

    private static async Task ConfirmReset(ChannelsConfigViewModel vm, ChannelType type)
    {
        vm.OpenAdapterManagement(type);
        var resetIndex = vm.GetManagementMenuItems()
            .Select((item, index) => (item, index))
            .Single(entry => entry.item.Action == ChannelsManagementAction.ResetConnection)
            .index;
        vm.MoveManagementMenu(resetIndex);
        vm.ActivateManagementMenuItem();
        vm.MoveResetConfirmation(1);
        await vm.ApplyResetConfirmationAsync(TestContext.Current.CancellationToken);
    }

    private static void MoveToManagementAction(ChannelsConfigViewModel vm, ChannelsManagementAction action)
    {
        var index = vm.GetManagementMenuItems()
            .Select((item, itemIndex) => (item, itemIndex))
            .Single(entry => entry.item.Action == action)
            .itemIndex;

        vm.MoveManagementMenu(index);
    }

    // Arranges a Slack adapter parked on the reset-confirmation screen with a background label refresh
    // genuinely in flight (a 5-minute probe delay holds it open). Shared by the reset tests that verify
    // the reset cancels-and-awaits that refresh without racing its write or deadlocking. The caller owns
    // disposal of the returned view-model.
    private ChannelsConfigViewModel ArrangeSlackResetWithLabelRefreshInFlight()
    {
        WriteAllChannelConfig();
        WriteAllChannelSecrets();
        var slackProbe = new FakeSlackProbe
        {
            DelayBeforeResult = TimeSpan.FromMinutes(5),
            NextResolutionResult = new SlackChannelResolutionResult(
                true, null, [new ResolvedSlackChannel("general", "C01")], []),
        };
        var vm = CreateViewModel(slackProbe: slackProbe);

        // Enter Manage Channels to start the background label refresh, then leave it in flight.
        vm.OpenAdapterManagement(ChannelType.Slack);
        MoveToManagementAction(vm, ChannelsManagementAction.ManageChannels);
        vm.ActivateManagementMenuItem();
        Assert.False(vm.PendingLabelRefresh?.IsCompleted ?? true); // background is in flight

        vm.GoBack();
        MoveToManagementAction(vm, ChannelsManagementAction.ResetConnection);
        vm.ActivateManagementMenuItem();
        vm.MoveResetConfirmation(1);
        return vm;
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
