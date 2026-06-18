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

/// <summary>
/// Which sub-screen the Telemetry &amp; Alerting editor is showing.
/// </summary>
internal enum TelemetryConfigScreen
{
    /// <summary>OTLP toggle/endpoint rows plus the outbound-webhook list.</summary>
    List,

    /// <summary>The add/edit form for a single outbound webhook.</summary>
    WebhookForm
}

/// <summary>
/// A read-model row for one configured outbound webhook in the list editor.
/// </summary>
internal sealed record TelemetryWebhookRow(string Name, string Url, WebhookFormat Format, bool HasAuthHeader);

/// <summary>
/// Telemetry &amp; Alerting editor. Keeps the OTLP enable/endpoint rows and exposes
/// <see cref="NotificationsConfig.Webhooks"/> as a multi-entry list editor (the
/// earlier revision surfaced only a single webhook). Each webhook carries a name,
/// URL, and one optional Authorization-style header (masked); the payload format
/// is auto-detected from the URL and shown read-only. Delivery policy
/// (dedup/retries/timeout) is intentionally out of scope and preserved untouched.
/// </summary>
internal sealed class TelemetryAlertingConfigViewModel : ReactiveViewModel
{
    private const string DefaultOtlpEndpoint = "http://127.0.0.1:4317";

    private readonly NetclawPaths _paths;
    private string _acceptedOtlpEndpoint;
    private int? _editingWebhookIndex;

    public TelemetryAlertingConfigViewModel(NetclawPaths paths)
    {
        _paths = paths;
        // Degrade to default telemetry state on a malformed/unreadable netclaw.json rather than
        // throwing from the constructor (which would make the Telemetry page permanently inaccessible).
        string? loadError = null;
        (bool TelemetryEnabled, string OtlpEndpoint, IReadOnlyList<TelemetryWebhookRow> Webhooks) state;
        try
        {
            state = LoadState(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            state = (false, DefaultOtlpEndpoint, []);
            loadError = $"Could not read netclaw.json: {ex.Message}";
        }
        TelemetryEnabled = new ReactiveProperty<bool>(state.TelemetryEnabled);
        OtlpEndpointDraft = new ReactiveProperty<string>(state.OtlpEndpoint);
        _acceptedOtlpEndpoint = state.OtlpEndpoint;
        Webhooks = new ReactiveProperty<IReadOnlyList<TelemetryWebhookRow>>(state.Webhooks);
        Screen = new ReactiveProperty<TelemetryConfigScreen>(TelemetryConfigScreen.List);
        SelectedRow = new ReactiveProperty<int>(0);
        FormFieldIndex = new ReactiveProperty<int>(0);
        WebhookNameDraft = new ReactiveProperty<string>(string.Empty);
        WebhookUrlDraft = new ReactiveProperty<string>(string.Empty);
        WebhookAuthHeaderDraft = new ReactiveProperty<string>(string.Empty);
        EditingHasPersistedAuthHeader = new ReactiveProperty<bool>(false);
        Status = new ReactiveProperty<ConfigStatusMessage>(loadError is null
            ? new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral)
            : new ConfigStatusMessage(loadError, ConfigStatusTone.Error));
        IsSaved = new ReactiveProperty<bool>(false);
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<bool> TelemetryEnabled { get; }
    public ReactiveProperty<string> OtlpEndpointDraft { get; }
    public ReactiveProperty<IReadOnlyList<TelemetryWebhookRow>> Webhooks { get; }
    public ReactiveProperty<TelemetryConfigScreen> Screen { get; }
    public ReactiveProperty<int> SelectedRow { get; }
    public ReactiveProperty<int> FormFieldIndex { get; }
    public ReactiveProperty<string> WebhookNameDraft { get; }
    public ReactiveProperty<string> WebhookUrlDraft { get; }
    public ReactiveProperty<string> WebhookAuthHeaderDraft { get; }
    public ReactiveProperty<bool> EditingHasPersistedAuthHeader { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<bool> IsSaved { get; }

    // List layout: 2 OTLP rows + one row per webhook + an "Add webhook" row.
    public const int OtlpRowCount = 2;
    public int WebhookCount => Webhooks.Value.Count;
    public int AddRowIndex => OtlpRowCount + WebhookCount;
    public int ListRowCount => AddRowIndex + 1;

    public bool IsWebhookRow(int index) => index >= OtlpRowCount && index < AddRowIndex;
    public int WebhookIndexFor(int row) => row - OtlpRowCount;

    public static readonly IReadOnlyList<string> FormFields = ["Name", "URL", "Auth header"];

    public void MoveSelection(int delta)
    {
        if (Screen.Value == TelemetryConfigScreen.WebhookForm)
        {
            FormFieldIndex.Value = (FormFieldIndex.Value + delta + FormFields.Count) % FormFields.Count;
            return;
        }

        var next = Math.Clamp(SelectedRow.Value + delta, 0, ListRowCount - 1);
        if (next != SelectedRow.Value)
            SelectedRow.Value = next;
    }

    /// <summary>Toggles telemetry from the OTLP-enabled row and autosaves.</summary>
    public bool ToggleTelemetry()
    {
        var previous = TelemetryEnabled.Value;
        TelemetryEnabled.Value = !TelemetryEnabled.Value;
        if (AutosaveCompletedAction("Telemetry enabled state saved."))
            return true;

        TelemetryEnabled.Value = previous;
        IsSaved.Value = false;
        RequestRedraw();
        return false;
    }

    public void AppendText(string text)
    {
        if (Screen.Value == TelemetryConfigScreen.WebhookForm)
        {
            FormFieldDraft.Value += text;
            MarkDirty();
            return;
        }

        if (SelectedRow.Value == 1)
        {
            if (OtlpEndpointDraft.Value == _acceptedOtlpEndpoint)
                OtlpEndpointDraft.Value = string.Empty;

            OtlpEndpointDraft.Value += text;
            MarkDirty();
        }
    }

    public void Backspace()
    {
        if (Screen.Value == TelemetryConfigScreen.WebhookForm)
        {
            var draft = FormFieldDraft;
            if (draft.Value.Length > 0)
            {
                draft.Value = draft.Value[..^1];
                MarkDirty();
            }

            return;
        }

        if (SelectedRow.Value == 1 && OtlpEndpointDraft.Value.Length > 0)
        {
            OtlpEndpointDraft.Value = OtlpEndpointDraft.Value[..^1];
            MarkDirty();
        }
    }

    private ReactiveProperty<string> FormFieldDraft => FormFieldIndex.Value switch
    {
        0 => WebhookNameDraft,
        1 => WebhookUrlDraft,
        _ => WebhookAuthHeaderDraft
    };

    /// <summary>Format auto-detected from the in-progress URL draft (read-only).</summary>
    public WebhookFormat DraftFormat => WebhookFormatDetection.InferFromUrl(WebhookUrlDraft.Value);

    public void ActivateSelected()
    {
        if (Screen.Value == TelemetryConfigScreen.WebhookForm)
        {
            SaveWebhookForm();
            return;
        }

        switch (SelectedRow.Value)
        {
            case 0:
                ToggleTelemetry();
                break;
            case 1:
                Save();
                break;
            default:
                if (SelectedRow.Value == AddRowIndex)
                    BeginAddWebhook();
                else if (IsWebhookRow(SelectedRow.Value))
                    BeginEditWebhook(WebhookIndexFor(SelectedRow.Value));
                break;
        }
    }

    public void BeginAddWebhook()
    {
        _editingWebhookIndex = null;
        WebhookNameDraft.Value = string.Empty;
        WebhookUrlDraft.Value = string.Empty;
        WebhookAuthHeaderDraft.Value = string.Empty;
        EditingHasPersistedAuthHeader.Value = false;
        FormFieldIndex.Value = 0;
        ClearStatus();
        Screen.Value = TelemetryConfigScreen.WebhookForm;
        RequestRedraw();
    }

    public void BeginEditWebhook(int index)
    {
        var rows = Webhooks.Value;
        if (index < 0 || index >= rows.Count)
            return;

        var row = rows[index];
        _editingWebhookIndex = index;
        WebhookNameDraft.Value = row.Name;
        WebhookUrlDraft.Value = row.Url;
        WebhookAuthHeaderDraft.Value = string.Empty;
        EditingHasPersistedAuthHeader.Value = row.HasAuthHeader;
        FormFieldIndex.Value = 0;
        ClearStatus();
        Screen.Value = TelemetryConfigScreen.WebhookForm;
        RequestRedraw();
    }

    public void RemoveSelectedWebhook()
    {
        if (!IsWebhookRow(SelectedRow.Value))
            return;

        var index = WebhookIndexFor(SelectedRow.Value);
        var removedName = Webhooks.Value[index].Name;
        if (PersistWebhooks(webhooks => webhooks.RemoveAt(index), $"Removed {removedName}. Saved."))
            SelectedRow.Value = Math.Clamp(SelectedRow.Value, 0, ListRowCount - 1);
    }

    public void CancelWebhookForm()
    {
        Screen.Value = TelemetryConfigScreen.List;
        ClearStatus();
        RequestRedraw();
    }

    private void SaveWebhookForm()
    {
        var url = WebhookUrlDraft.Value.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            Status.Value = new ConfigStatusMessage("Outbound webhook URL is required.", ConfigStatusTone.Error);
            RequestRedraw();
            return;
        }

        if (!TryValidateHttpUri(url, "Outbound webhook URL", out var normalizedUrl, out var urlError))
        {
            Status.Value = new ConfigStatusMessage(urlError, ConfigStatusTone.Error);
            RequestRedraw();
            return;
        }

        var authDraft = WebhookAuthHeaderDraft.Value.Trim();
        // A single "-" explicitly clears a persisted auth header; a blank field preserves it. Without
        // this gesture there is no way to remove a header once set (blank always means "keep").
        var clearAuth = authDraft == "-";
        string? headerName = null;
        string? headerValue = null;
        if (!clearAuth
            && !string.IsNullOrWhiteSpace(authDraft)
            && !TryParseHeader(authDraft, out headerName, out headerValue, out var headerError))
        {
            Status.Value = new ConfigStatusMessage(headerError, ConfigStatusTone.Error);
            RequestRedraw();
            return;
        }

        var name = string.IsNullOrWhiteSpace(WebhookNameDraft.Value)
            ? $"{WebhookFormatDetection.InferFromUrl(normalizedUrl!).ToString().ToLowerInvariant()}-webhook"
            : WebhookNameDraft.Value.Trim();

        var editing = _editingWebhookIndex;
        var newAuth = !clearAuth && !string.IsNullOrWhiteSpace(authDraft);
        var verb = editing is null ? "added" : "updated";
        var saved = PersistWebhooks(webhooks =>
        {
            var target = editing is { } index && index < webhooks.Count
                ? webhooks[index]
                : new WebhookTarget();

            target.Name = name;
            target.Url = normalizedUrl!;
            target.Format = WebhookFormatDetection.InferFromUrl(normalizedUrl!);
            if (newAuth)
            {
                target.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [headerName!] = headerValue!
                };
            }
            else if (clearAuth)
            {
                target.Headers = null;
            }

            // Otherwise (blank, no "-"): leave target.Headers untouched so an unedited header is kept.

            if (editing is null)
                webhooks.Add(target);
        }, $"Webhook {name} {verb}. Saved.");

        if (saved)
        {
            Screen.Value = TelemetryConfigScreen.List;
            SelectedRow.Value = editing is { } idx
                ? OtlpRowCount + idx
                : OtlpRowCount + Math.Max(0, WebhookCount - 1);
        }
    }

    public bool Save()
        => Save("Telemetry & Alerting settings saved.");

    private bool Save(string successMessage)
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

        return ConfigAutosave.Run(
            () =>
            {
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

                ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, root);
                ReloadState(successMessage, resetOtlpDraft: true);
                return true;
            },
            Status,
            "Telemetry & Alerting save failed",
            RequestRedraw);
    }

    /// <summary>
    /// Mutates the persisted <see cref="NotificationsConfig.Webhooks"/> list through
    /// the same section-preserving writer the rest of the editor uses, leaving the
    /// delivery policy and unrelated sections untouched.
    /// </summary>
    private bool PersistWebhooks(Action<List<WebhookTarget>> mutate, string successMessage)
        => ConfigAutosave.Run(
            () =>
            {
                var root = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
                root["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
                var notifications = ConfigFileHelper.LoadSection<NotificationsConfig>(root, "Notifications");
                mutate(notifications.Webhooks);

                if (notifications.Webhooks.Count > 0
                    || root.ContainsKey("Notifications"))
                {
                    root["Notifications"] = BuildNotificationsSection(notifications);
                }

                ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, root);
                ReloadState(successMessage, resetOtlpDraft: false);
                return true;
            },
            Status,
            "Telemetry & Alerting autosave failed",
            RequestRedraw);

    private void ReloadState(string successMessage, bool resetOtlpDraft)
    {
        var state = LoadState(_paths);
        TelemetryEnabled.Value = state.TelemetryEnabled;
        _acceptedOtlpEndpoint = state.OtlpEndpoint;
        Webhooks.Value = state.Webhooks;

        if (resetOtlpDraft)
        {
            // The OTLP endpoint was just persisted: sync the draft to it and mark fully saved.
            OtlpEndpointDraft.Value = state.OtlpEndpoint;
            IsSaved.Value = true;
        }
        else
        {
            // A different section (a webhook) was saved. Preserve any in-progress OTLP endpoint edit
            // and report fully-saved only when that draft is not dirty — never discard the edit or
            // falsely flip IsSaved=true over it.
            IsSaved.Value = OtlpEndpointDraft.Value == state.OtlpEndpoint;
        }

        Status.Value = new ConfigStatusMessage(successMessage, ConfigStatusTone.Success);
        RequestRedraw();
    }

    private bool AutosaveCompletedAction(string successMessage)
        => ConfigAutosave.Run(
            () => Save(successMessage),
            Status,
            "Telemetry & Alerting autosave failed",
            RequestRedraw);

    public void GoBack()
    {
        if (Screen.Value == TelemetryConfigScreen.WebhookForm)
        {
            CancelWebhookForm();
            return;
        }

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
        Webhooks.Dispose();
        Screen.Dispose();
        SelectedRow.Dispose();
        FormFieldIndex.Dispose();
        WebhookNameDraft.Dispose();
        WebhookUrlDraft.Dispose();
        WebhookAuthHeaderDraft.Dispose();
        EditingHasPersistedAuthHeader.Dispose();
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

    private static (bool TelemetryEnabled, string OtlpEndpoint, IReadOnlyList<TelemetryWebhookRow> Webhooks) LoadState(NetclawPaths paths)
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

        var notifications = ConfigFileHelper.LoadSection<NotificationsConfig>(root, "Notifications");
        var rows = notifications.Webhooks
            .Select(static webhook => new TelemetryWebhookRow(
                string.IsNullOrWhiteSpace(webhook.Name) ? "(unnamed)" : webhook.Name,
                webhook.Url,
                webhook.Format,
                webhook.Headers is { Count: > 0 }))
            .ToArray();

        return (enabled, endpoint, rows);
    }

    private static Dictionary<string, object> LoadRawSection(Dictionary<string, object> root, string sectionName)
    {
        if (!root.TryGetValue(sectionName, out var raw) || raw is null)
            return [];

        if (raw is JsonElement element)
            return JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText(), JsonDefaults.ConfigRead) ?? [];

        return raw as Dictionary<string, object> ?? [];
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
