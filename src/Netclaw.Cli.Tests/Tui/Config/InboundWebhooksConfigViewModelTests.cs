// -----------------------------------------------------------------------
// <copyright file="InboundWebhooksConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class InboundWebhooksConfigViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public InboundWebhooksConfigViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Inbound_webhooks_dashboard_entry_routes_to_real_editor()
    {
        using var vm = new Netclaw.Cli.Tui.ConfigDashboardViewModel(new Netclaw.Cli.Tui.ConfigDashboardNavigationState());
        string? route = null;
        vm.RouteRequested = r => route = r;

        vm.Activate(vm.Items.Single(static item => item.Label == "Inbound Webhooks"));

        Assert.Equal("/inbound-webhooks", route);
    }

    [Fact]
    public void Save_persists_global_enablement_and_timeout_for_runtime_binding()
    {
        WriteValidRoute("github-issues");
        using var vm = new InboundWebhooksConfigViewModel(_paths);

        vm.ToggleEnabled();
        vm.SelectedRow.Value = 1;
        vm.AppendTimeoutText("120");

        Assert.True(vm.Save());

        var bound = BindWebhooksConfig();
        Assert.True(bound.Enabled);
        Assert.Equal(120, bound.ExecutionTimeoutSeconds);
    }

    [Fact]
    public void Save_rejects_invalid_timeout_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new InboundWebhooksConfigViewModel(_paths);
        vm.SelectedRow.Value = 1;

        vm.AppendTimeoutText("0");

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("between 1 and 3600", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_blocks_enabled_state_when_no_valid_routes_exist()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new InboundWebhooksConfigViewModel(_paths);

        vm.ToggleEnabled();

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("at least one valid route", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Empty(Directory.EnumerateFiles(_paths.WebhooksDirectory));
    }

    [Fact]
    public void Disabled_save_does_not_create_dummy_routes()
    {
        using var vm = new InboundWebhooksConfigViewModel(_paths);

        Assert.True(vm.Save());

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Webhooks.Enabled", out var enabled));
        Assert.Equal(false, enabled);
        Assert.Empty(Directory.EnumerateFiles(_paths.WebhooksDirectory));
    }

    private void WriteValidRoute(string name)
    {
        var store = new WebhookRouteStore(_paths);
        store.Save(name, new WebhookRouteConfig
        {
            Prompt = "triage this webhook",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString("secret")
            }
        });
    }

    private WebhooksConfig BindWebhooksConfig()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(_paths.NetclawConfigPath)
            .Build();
        return configuration.GetSection("Webhooks").Get<WebhooksConfig>()!;
    }
}
