// -----------------------------------------------------------------------
// <copyright file="TelemetryAlertingConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class TelemetryAlertingConfigViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public TelemetryAlertingConfigViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Telemetry_alerting_dashboard_entry_routes_to_real_editor()
    {
        using var vm = new Netclaw.Cli.Tui.ConfigDashboardViewModel(new Netclaw.Cli.Tui.ConfigDashboardNavigationState());
        string? route = null;
        vm.RouteRequested = r => route = r;

        vm.Activate(vm.Items.Single(static item => item.Label == "Telemetry & Alerting"));

        Assert.Equal("/telemetry-alerting", route);
    }

    [Fact]
    public void Save_persists_telemetry_and_outbound_webhook_for_runtime_binding()
    {
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.ToggleTelemetry();
        vm.SelectedRow.Value = 1;
        vm.AppendText("http://127.0.0.1:4318");
        vm.SelectedRow.Value = 2;
        vm.AppendText("https://hooks.slack.com/services/T000/B000/SECRET");

        Assert.True(vm.Save());

        var telemetry = Bind<TelemetryOptions>("Telemetry");
        Assert.True(telemetry.Enabled);
        Assert.Equal("http://127.0.0.1:4318", telemetry.Otlp.Endpoint);

        var notifications = Bind<NotificationsConfig>("Notifications");
        var webhook = Assert.Single(notifications.Webhooks);
        Assert.Equal("ops-alerts", webhook.Name);
        Assert.Equal("https://hooks.slack.com/services/T000/B000/SECRET", webhook.Url);
        Assert.Equal(WebhookFormat.Slack, webhook.Format);
    }

    [Fact]
    public void Save_rejects_invalid_telemetry_endpoint_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new TelemetryAlertingConfigViewModel(_paths);
        vm.SelectedRow.Value = 1;
        vm.OtlpEndpointDraft.Value = "not-a-url";

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("absolute HTTP or HTTPS URI", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_rejects_invalid_outbound_webhook_url_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new TelemetryAlertingConfigViewModel(_paths);
        vm.SelectedRow.Value = 2;
        vm.AppendText("ftp://alerts.example.test/hook");

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("absolute HTTP or HTTPS URI", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_rejects_invalid_outbound_auth_header_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new TelemetryAlertingConfigViewModel(_paths);
        vm.SelectedRow.Value = 3;
        vm.AppendText("Bearer token-without-header-name");

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("Header-Name", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_preserves_webhook_headers_delivery_policy_and_unrelated_secrets()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"Notifications\":{\"DeduplicationWindowSeconds\":120,\"MaxRetries\":4,\"TimeoutSeconds\":12,\"Webhooks\":[{\"Name\":\"ops-alerts\",\"Url\":\"https://old.example.test/hook\",\"Headers\":{\"Authorization\":\"Bearer old\"},\"Format\":\"Generic\"}]}}");
        File.WriteAllText(_paths.SecretsPath, "{\"Slack\":{\"BotToken\":\"ENC:slack\"}}");
        var beforeSecrets = File.ReadAllText(_paths.SecretsPath);
        using var vm = new TelemetryAlertingConfigViewModel(_paths);
        vm.SelectedRow.Value = 2;
        vm.AppendText("https://new.example.test/hook");

        Assert.True(vm.Save());

        var notifications = Bind<NotificationsConfig>("Notifications");
        Assert.Equal(120, notifications.DeduplicationWindowSeconds);
        Assert.Equal(4, notifications.MaxRetries);
        Assert.Equal(12, notifications.TimeoutSeconds);
        var webhook = Assert.Single(notifications.Webhooks);
        Assert.Equal("https://new.example.test/hook", webhook.Url);
        Assert.Equal("Bearer old", webhook.Headers?["Authorization"]);
        Assert.Equal(beforeSecrets, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public void Save_updates_outbound_auth_header_when_nonblank_header_is_entered()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"Notifications\":{\"Webhooks\":[{\"Name\":\"ops-alerts\",\"Url\":\"https://alerts.example.test/hook\",\"Headers\":{\"Authorization\":\"Bearer old\"},\"Format\":\"Generic\"}]}}");
        using var vm = new TelemetryAlertingConfigViewModel(_paths);
        vm.SelectedRow.Value = 3;
        vm.AppendText("Authorization: Bearer new");

        Assert.True(vm.Save());

        var webhook = Assert.Single(Bind<NotificationsConfig>("Notifications").Webhooks);
        Assert.Equal("Bearer new", webhook.Headers?["Authorization"]);
    }

    private T Bind<T>(string sectionName) where T : new()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(_paths.NetclawConfigPath)
            .Build();
        return configuration.GetSection(sectionName).Get<T>() ?? new T();
    }
}
