// -----------------------------------------------------------------------
// <copyright file="TelemetryAlertingConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

internal sealed class TelemetryAlertingConfigViewModel : ReactiveViewModel
{
    private const string DefaultOtlpEndpoint = "http://127.0.0.1:4317";
    private const string DefaultWebhookName = "ops-alerts";

    private readonly NetclawPaths _paths;
    private string _acceptedOtlpEndpoint;

    public TelemetryAlertingConfigViewModel(NetclawPaths paths)
    {
        _paths = paths;
        var state = LoadState(paths);
        TelemetryEnabled = new ReactiveProperty<bool>(state.TelemetryEnabled);
        OtlpEndpointDraft = new ReactiveProperty<string>(state.OtlpEndpoint);
        _acceptedOtlpEndpoint = state.OtlpEndpoint;
        OutboundWebhookCount = new ReactiveProperty<int>(state.OutboundWebhookCount);
        OutboundWebhookUrlDraft = new ReactiveProperty<string>(string.Empty);
        OutboundWebhookAuthHeaderDraft = new ReactiveProperty<string>(string.Empty);
        HasPersistedWebhookAuthHeader = new ReactiveProperty<bool>(state.HasPersistedWebhookAuthHeader);
        SelectedRow = new ReactiveProperty<int>(0);
        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        IsSaved = new ReactiveProperty<bool>(false);
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<bool> TelemetryEnabled { get; }
    public ReactiveProperty<string> OtlpEndpointDraft { get; }
    public ReactiveProperty<int> OutboundWebhookCount { get; }
    public ReactiveProperty<string> OutboundWebhookUrlDraft { get; }
    public ReactiveProperty<string> OutboundWebhookAuthHeaderDraft { get; }
    public ReactiveProperty<bool> HasPersistedWebhookAuthHeader { get; }
    public ReactiveProperty<int> SelectedRow { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<bool> IsSaved { get; }

    public IReadOnlyList<string> Rows { get; } =
    [
        "Telemetry enabled",
        "OTLP endpoint",
        "Outbound webhook URL",
        "Outbound webhook auth header",
        "Save"
    ];

    public void MoveSelection(int delta)
    {
        var next = Math.Clamp(SelectedRow.Value + delta, 0, Rows.Count - 1);
        if (next != SelectedRow.Value)
            SelectedRow.Value = next;
    }

    public void ToggleTelemetry()
    {
        TelemetryEnabled.Value = !TelemetryEnabled.Value;
        MarkDirty();
    }

    public void AppendText(string text)
    {
        switch (SelectedRow.Value)
        {
            case 1:
                if (OtlpEndpointDraft.Value == _acceptedOtlpEndpoint)
                    OtlpEndpointDraft.Value = string.Empty;

                OtlpEndpointDraft.Value += text;
                break;
            case 2:
                OutboundWebhookUrlDraft.Value += text;
                break;
            case 3:
                OutboundWebhookAuthHeaderDraft.Value += text;
                break;
            default:
                return;
        }

        MarkDirty();
    }

    public void Backspace()
    {
        var target = SelectedRow.Value switch
        {
            1 => OtlpEndpointDraft,
            2 => OutboundWebhookUrlDraft,
            3 => OutboundWebhookAuthHeaderDraft,
            _ => null
        };

        if (target is null || target.Value.Length == 0)
            return;

        target.Value = target.Value[..^1];
        MarkDirty();
    }

    public void ActivateSelected()
    {
        switch (SelectedRow.Value)
        {
            case 0:
                ToggleTelemetry();
                break;
            case 4:
                Save();
                break;
        }
    }

    public bool Save()
    {
        var endpoint = string.IsNullOrWhiteSpace(OtlpEndpointDraft.Value)
            ? DefaultOtlpEndpoint
            : OtlpEndpointDraft.Value.Trim();
        if (!TryValidateHttpUri(endpoint, "OTLP endpoint", out var normalizedEndpoint, out var endpointError))
        {
            Status.Value = new ConfigStatusMessage(endpointError, ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        var webhookUrlDraft = OutboundWebhookUrlDraft.Value.Trim();
        string? normalizedWebhookUrl = null;
        if (!string.IsNullOrWhiteSpace(webhookUrlDraft)
            && !TryValidateHttpUri(webhookUrlDraft, "Outbound webhook URL", out normalizedWebhookUrl, out var webhookError))
        {
            Status.Value = new ConfigStatusMessage(webhookError, ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        var authHeaderDraft = OutboundWebhookAuthHeaderDraft.Value.Trim();
        string? headerName = null;
        string? headerValue = null;
        if (!string.IsNullOrWhiteSpace(authHeaderDraft)
            && !TryParseHeader(authHeaderDraft, out headerName, out headerValue, out var headerError))
        {
            Status.Value = new ConfigStatusMessage(headerError, ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        var root = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        root["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        root["Telemetry"] = new Dictionary<string, object>
        {
            ["Enabled"] = TelemetryEnabled.Value,
            ["Otlp"] = new Dictionary<string, object>
            {
                ["Endpoint"] = normalizedEndpoint!
            }
        };

        var notifications = LoadSection<NotificationsConfig>(root, "Notifications");
        if (normalizedWebhookUrl is not null || !string.IsNullOrWhiteSpace(authHeaderDraft))
        {
            var target = notifications.Webhooks.FirstOrDefault(static w => string.Equals(w.Name, DefaultWebhookName, StringComparison.OrdinalIgnoreCase))
                ?? notifications.Webhooks.FirstOrDefault()
                ?? new WebhookTarget { Name = DefaultWebhookName };

            notifications.Webhooks.Remove(target);
            target.Name ??= DefaultWebhookName;
            if (normalizedWebhookUrl is not null)
            {
                target.Url = normalizedWebhookUrl;
                target.Format = WebhookFormatDetection.InferFromUrl(normalizedWebhookUrl);
            }

            if (!string.IsNullOrWhiteSpace(authHeaderDraft))
            {
                target.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [headerName!] = headerValue!
                };
            }

            notifications.Webhooks.Add(target);
        }

        if (notifications.Webhooks.Count > 0)
            root["Notifications"] = BuildNotificationsSection(notifications);

        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, root);

        var state = LoadState(_paths);
        TelemetryEnabled.Value = state.TelemetryEnabled;
        OtlpEndpointDraft.Value = state.OtlpEndpoint;
        _acceptedOtlpEndpoint = state.OtlpEndpoint;
        OutboundWebhookCount.Value = state.OutboundWebhookCount;
        HasPersistedWebhookAuthHeader.Value = state.HasPersistedWebhookAuthHeader;
        OutboundWebhookUrlDraft.Value = string.Empty;
        OutboundWebhookAuthHeaderDraft.Value = string.Empty;
        IsSaved.Value = true;
        Status.Value = new ConfigStatusMessage("Telemetry & Alerting settings saved.", ConfigStatusTone.Success);
        RequestRedraw();
        return true;
    }

    public void GoBack()
    {
        RouteRequested?.Invoke("/config");
        Navigate?.Invoke("/config");
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    public override void Dispose()
    {
        TelemetryEnabled.Dispose();
        OtlpEndpointDraft.Dispose();
        OutboundWebhookCount.Dispose();
        OutboundWebhookUrlDraft.Dispose();
        OutboundWebhookAuthHeaderDraft.Dispose();
        HasPersistedWebhookAuthHeader.Dispose();
        SelectedRow.Dispose();
        Status.Dispose();
        IsSaved.Dispose();
        base.Dispose();
    }

    private void MarkDirty()
    {
        IsSaved.Value = false;
        ClearStatus();
        RequestRedraw();
    }

    private void ClearStatus()
    {
        if (!string.IsNullOrWhiteSpace(Status.Value.Text))
            Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
    }

    private static bool TryValidateHttpUri(string value, string label, out string? normalized, out string error)
    {
        normalized = null;
        error = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            error = $"{label} must be an absolute HTTP or HTTPS URI.";
            return false;
        }

        normalized = uri.ToString().TrimEnd('/');
        return true;
    }

    private static bool TryParseHeader(string value, out string? name, out string? headerValue, out string error)
    {
        name = null;
        headerValue = null;
        error = string.Empty;
        if (value.Contains('\r') || value.Contains('\n'))
        {
            error = "Outbound webhook auth header must be a single line.";
            return false;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            error = "Outbound webhook auth header must use 'Header-Name: value' format.";
            return false;
        }

        name = value[..separator].Trim();
        headerValue = value[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(headerValue))
        {
            error = "Outbound webhook auth header name and value are required.";
            return false;
        }

        return true;
    }

    private static (bool TelemetryEnabled, string OtlpEndpoint, int OutboundWebhookCount, bool HasPersistedWebhookAuthHeader) LoadState(NetclawPaths paths)
    {
        var root = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var telemetry = LoadRawSection(root, "Telemetry");
        var enabled = ConfigFileHelper.TryGetPathValue(telemetry, "Enabled", out var enabledValue)
            && enabledValue is bool enabledFlag
            && enabledFlag;
        var endpoint = ConfigFileHelper.TryGetPathValue(telemetry, "Otlp.Endpoint", out var endpointValue)
            && endpointValue is string endpointText
            && !string.IsNullOrWhiteSpace(endpointText)
                ? endpointText
                : DefaultOtlpEndpoint;

        var notifications = LoadSection<NotificationsConfig>(root, "Notifications");
        return (enabled, endpoint, notifications.Webhooks.Count, notifications.Webhooks.Any(static w => w.Headers is { Count: > 0 }));
    }

    private static Dictionary<string, object> LoadRawSection(Dictionary<string, object> root, string sectionName)
    {
        if (!root.TryGetValue(sectionName, out var raw) || raw is null)
            return [];

        if (raw is JsonElement element)
            return JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText(), JsonDefaults.ConfigRead) ?? [];

        return raw as Dictionary<string, object> ?? [];
    }

    private static T LoadSection<T>(Dictionary<string, object> root, string sectionName) where T : new()
    {
        if (!root.TryGetValue(sectionName, out var raw) || raw is null)
            return new T();

        var json = raw is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(raw, JsonDefaults.ConfigFile);
        return JsonSerializer.Deserialize<T>(json, JsonDefaults.ConfigRead) ?? new T();
    }

    private static Dictionary<string, object> BuildNotificationsSection(NotificationsConfig config)
        => new()
        {
            ["DeduplicationWindowSeconds"] = config.DeduplicationWindowSeconds,
            ["MaxRetries"] = config.MaxRetries,
            ["TimeoutSeconds"] = config.TimeoutSeconds,
            ["Webhooks"] = config.Webhooks.Select(static webhook =>
            {
                var item = new Dictionary<string, object>
                {
                    ["Url"] = webhook.Url,
                    ["Format"] = webhook.Format.ToString()
                };

                if (!string.IsNullOrWhiteSpace(webhook.Name))
                    item["Name"] = webhook.Name;
                if (webhook.Headers is { Count: > 0 })
                    item["Headers"] = webhook.Headers;

                return (object)item;
            }).ToArray()
        };
}
