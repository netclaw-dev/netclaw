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
    public void Constructor_with_malformed_config_does_not_throw_and_surfaces_error()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ not valid json ");

        // Must not throw from the constructor (which would make the page permanently inaccessible).
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
    }

    [Fact]
    public void Save_persists_telemetry_otlp_endpoint_for_runtime_binding()
    {
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.ToggleTelemetry();
        vm.SelectedRow.Value = 1;
        vm.AppendText("http://127.0.0.1:4318");

        Assert.True(vm.Save());

        var telemetry = Bind<TelemetryOptions>("Telemetry");
        Assert.True(telemetry.Enabled);
        Assert.Equal("http://127.0.0.1:4318", telemetry.Otlp.Endpoint);
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
    public void Save_surfaces_write_failure_without_crashing_the_loop()
    {
        using var vm = new TelemetryAlertingConfigViewModel(_paths);
        vm.ToggleTelemetry();
        vm.SelectedRow.Value = 1;
        vm.AppendText("http://127.0.0.1:4318");

        // Force the config write to fail like a disk-full / permission-denied failure would: AtomicFile
        // cannot replace a path that is a directory. LoadJsonDict treats it as missing, so only the
        // WriteConfigFile throws — which was previously unguarded on this direct Save() path.
        File.Delete(_paths.NetclawConfigPath);
        Directory.CreateDirectory(_paths.NetclawConfigPath);

        // Must not throw into the Termina event loop: the write is now wrapped in ConfigAutosave.Run.
        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
    }

    [Fact]
    public void Saving_a_webhook_preserves_an_in_progress_otlp_endpoint_draft()
    {
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        // Type an OTLP endpoint but never save it — it stays an unsaved draft.
        vm.SelectedRow.Value = 1;
        vm.AppendText("http://unsaved.example.test:4318");

        // Save a webhook (a different section). This used to ReloadState unconditionally, discarding
        // the dirty OTLP draft and force-flipping IsSaved=true.
        vm.BeginAddWebhook();
        vm.WebhookNameDraft.Value = "ops";
        vm.WebhookUrlDraft.Value = "https://alerts.example.test/hook";
        vm.ActivateSelected();

        Assert.Single(Bind<NotificationsConfig>("Notifications").Webhooks);
        // The in-progress OTLP draft survives, and IsSaved reflects that it is still unsaved.
        Assert.Equal("http://unsaved.example.test:4318", vm.OtlpEndpointDraft.Value);
        Assert.False(vm.IsSaved.Value);
    }

    [Fact]
    public void Adding_a_webhook_persists_name_url_and_detected_slack_format()
    {
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.BeginAddWebhook();
        vm.WebhookNameDraft.Value = "pagerduty";
        vm.WebhookUrlDraft.Value = "https://hooks.slack.com/services/T000/B000/SECRET";
        vm.ActivateSelected();

        var webhook = Assert.Single(Bind<NotificationsConfig>("Notifications").Webhooks);
        Assert.Equal("pagerduty", webhook.Name);
        Assert.Equal("https://hooks.slack.com/services/T000/B000/SECRET", webhook.Url);
        Assert.Equal(WebhookFormat.Slack, webhook.Format);
        Assert.Equal(TelemetryConfigScreen.List, vm.Screen.Value);
    }

    [Fact]
    public void Adding_a_generic_url_defaults_format_and_name()
    {
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.BeginAddWebhook();
        vm.WebhookUrlDraft.Value = "https://alerts.example.test/hook";
        vm.ActivateSelected();

        var webhook = Assert.Single(Bind<NotificationsConfig>("Notifications").Webhooks);
        Assert.Equal("generic-webhook", webhook.Name);
        Assert.Equal(WebhookFormat.Generic, webhook.Format);
    }

    [Fact]
    public void Multiple_webhooks_round_trip_through_the_list_editor()
    {
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.BeginAddWebhook();
        vm.WebhookNameDraft.Value = "ops";
        vm.WebhookUrlDraft.Value = "https://alerts.example.test/ops";
        vm.ActivateSelected();

        vm.BeginAddWebhook();
        vm.WebhookNameDraft.Value = "slack";
        vm.WebhookUrlDraft.Value = "https://hooks.slack.com/services/T/B/C";
        vm.ActivateSelected();

        var webhooks = Bind<NotificationsConfig>("Notifications").Webhooks;
        Assert.Equal(2, webhooks.Count);
        Assert.Contains(webhooks, w => w.Name == "ops" && w.Format == WebhookFormat.Generic);
        Assert.Contains(webhooks, w => w.Name == "slack" && w.Format == WebhookFormat.Slack);

        // A fresh VM sees both entries in its list rows (reentrancy).
        using var reopened = new TelemetryAlertingConfigViewModel(_paths);
        Assert.Equal(2, reopened.WebhookCount);
    }

    [Fact]
    public void Editing_a_webhook_updates_url_and_preserves_stored_header_when_blank()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"Notifications\":{\"DeduplicationWindowSeconds\":120,\"MaxRetries\":4,\"TimeoutSeconds\":12,\"Webhooks\":[{\"Name\":\"ops-alerts\",\"Url\":\"https://old.example.test/hook\",\"Headers\":{\"Authorization\":\"Bearer old\"},\"Format\":\"Generic\"}]}}");
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.BeginEditWebhook(0);
        Assert.True(vm.EditingHasPersistedAuthHeader.Value);
        vm.WebhookUrlDraft.Value = "https://new.example.test/hook";
        vm.ActivateSelected();

        var notifications = Bind<NotificationsConfig>("Notifications");
        var webhook = Assert.Single(notifications.Webhooks);
        Assert.Equal("https://new.example.test/hook", webhook.Url);
        Assert.Equal("Bearer old", webhook.Headers?["Authorization"]);
        // Delivery policy is preserved untouched.
        Assert.Equal(120, notifications.DeduplicationWindowSeconds);
        Assert.Equal(4, notifications.MaxRetries);
        Assert.Equal(12, notifications.TimeoutSeconds);
    }

    [Fact]
    public void Editing_a_webhook_replaces_the_auth_header_when_a_nonblank_header_is_entered()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"Notifications\":{\"Webhooks\":[{\"Name\":\"ops-alerts\",\"Url\":\"https://alerts.example.test/hook\",\"Headers\":{\"Authorization\":\"Bearer old\"},\"Format\":\"Generic\"}]}}");
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.BeginEditWebhook(0);
        vm.WebhookAuthHeaderDraft.Value = "Authorization: Bearer new";
        vm.ActivateSelected();

        var webhook = Assert.Single(Bind<NotificationsConfig>("Notifications").Webhooks);
        Assert.Equal("Bearer new", webhook.Headers?["Authorization"]);
    }

    [Fact]
    public void Editing_a_webhook_clears_the_auth_header_with_the_dash_gesture()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"Notifications\":{\"Webhooks\":[{\"Name\":\"ops-alerts\",\"Url\":\"https://alerts.example.test/hook\",\"Headers\":{\"Authorization\":\"Bearer old\"},\"Format\":\"Generic\"}]}}");
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.BeginEditWebhook(0);
        Assert.True(vm.EditingHasPersistedAuthHeader.Value);
        // A blank field would preserve the header; "-" explicitly removes it.
        vm.WebhookAuthHeaderDraft.Value = "-";
        vm.ActivateSelected();

        var webhook = Assert.Single(Bind<NotificationsConfig>("Notifications").Webhooks);
        Assert.Null(webhook.Headers);
    }

    [Fact]
    public void Saving_a_webhook_without_a_url_is_rejected_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.BeginAddWebhook();
        vm.WebhookNameDraft.Value = "no-url";
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("URL is required", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TelemetryConfigScreen.WebhookForm, vm.Screen.Value);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Saving_a_webhook_with_a_non_http_url_is_rejected_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.BeginAddWebhook();
        vm.WebhookUrlDraft.Value = "ftp://alerts.example.test/hook";
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("absolute HTTP or HTTPS URI", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Saving_a_webhook_with_a_malformed_auth_header_is_rejected_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.BeginAddWebhook();
        vm.WebhookUrlDraft.Value = "https://alerts.example.test/hook";
        vm.WebhookAuthHeaderDraft.Value = "Bearer token-without-header-name";
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("Header-Name", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Removing_a_webhook_drops_only_the_selected_entry()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"Notifications\":{\"Webhooks\":[{\"Name\":\"ops\",\"Url\":\"https://a.test/h\",\"Format\":\"Generic\"},{\"Name\":\"slack\",\"Url\":\"https://hooks.slack.com/x\",\"Format\":\"Slack\"}]}}");
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        // Row 2 == first webhook (OtlpRowCount == 2).
        vm.SelectedRow.Value = TelemetryAlertingConfigViewModel.OtlpRowCount;
        vm.RemoveSelectedWebhook();

        var webhook = Assert.Single(Bind<NotificationsConfig>("Notifications").Webhooks);
        Assert.Equal("slack", webhook.Name);
    }

    [Fact]
    public void Webhook_edits_preserve_unrelated_secrets_file()
    {
        File.WriteAllText(_paths.SecretsPath, "{\"Slack\":{\"BotToken\":\"ENC:slack\"}}");
        var beforeSecrets = File.ReadAllText(_paths.SecretsPath);
        using var vm = new TelemetryAlertingConfigViewModel(_paths);

        vm.BeginAddWebhook();
        vm.WebhookUrlDraft.Value = "https://alerts.example.test/hook";
        vm.ActivateSelected();

        Assert.Equal(beforeSecrets, File.ReadAllText(_paths.SecretsPath));
    }

    private T Bind<T>(string sectionName) where T : new()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(_paths.NetclawConfigPath)
            .Build();
        return configuration.GetSection(sectionName).Get<T>() ?? new T();
    }
}
