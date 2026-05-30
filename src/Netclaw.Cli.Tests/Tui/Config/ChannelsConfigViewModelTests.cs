// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Config;
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
    public void Channels_page_lists_supported_chat_adapters()
    {
        using var vm = new ChannelsConfigViewModel(_paths);

        var labels = vm.Items.Select(static item => item.Label).ToArray();

        Assert.Equal(["Slack", "Discord", "Mattermost"], labels);
    }

    [Fact]
    public void Provider_summaries_reflect_current_config()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = new ChannelsConfigViewModel(_paths);

        var summaries = vm.Items.ToDictionary(static item => item.Label, static item => item.Summary);

        Assert.Equal("3 channels, 2 users", summaries["Slack"]);
        Assert.Equal("disabled", summaries["Discord"]);
        Assert.Equal("1 channel", summaries["Mattermost"]);
    }

    [Fact]
    public void Missing_provider_reports_not_configured()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Slack": { "Enabled": true, "AllowDirectMessages": true }
            }
            """);
        using var vm = new ChannelsConfigViewModel(_paths);

        var summaries = vm.Items.ToDictionary(static item => item.Label, static item => item.Summary);

        Assert.Equal("DMs only", summaries["Slack"]);
        Assert.Equal("not configured", summaries["Discord"]);
        Assert.Equal("not configured", summaries["Mattermost"]);
    }

    [Fact]
    public void Provider_details_show_config_and_secret_state()
    {
        WriteChannelConfig();
        WriteChannelSecrets();
        using var vm = new ChannelsConfigViewModel(_paths);
        vm.SelectedIndex.Value = 0;

        vm.OpenSelectedProvider();

        var details = vm.SelectedDetails.ToDictionary(static detail => detail.Label, static detail => detail.Value);
        Assert.Equal(ChannelsConfigMode.Details, vm.Mode.Value);
        Assert.Equal("enabled", details["Status"]);
        Assert.Equal("configured", details["Bot token"]);
        Assert.Equal("configured", details["App token"]);
        Assert.Equal("3 configured", details["Allowed channels"]);
        Assert.Equal("2 configured", details["Allowed users"]);
        Assert.Equal("enabled", details["DMs"]);
        Assert.Equal("2 configured", details["Audience overrides"]);
    }

    [Fact]
    public void Back_from_details_returns_to_provider_list()
    {
        using var vm = new ChannelsConfigViewModel(_paths);
        vm.OpenSelectedProvider();

        vm.GoBack();

        Assert.Equal(ChannelsConfigMode.Providers, vm.Mode.Value);
        Assert.False(vm.ShutdownRequestedForTest);
    }

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
}
