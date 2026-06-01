// -----------------------------------------------------------------------
// <copyright file="InboundWebhooksConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

internal sealed record InboundWebhookRouteSummary(int Total, int Enabled, int Disabled, int Invalid)
{
    public int Valid => Total - Invalid;
}

internal sealed class InboundWebhooksConfigViewModel : ReactiveViewModel
{
    private readonly NetclawPaths _paths;
    private readonly WebhookRouteStore _routeStore;
    private string _acceptedTimeoutText;

    public InboundWebhooksConfigViewModel(NetclawPaths paths)
    {
        _paths = paths;
        _routeStore = new WebhookRouteStore(paths);
        var config = LoadConfig();
        Enabled = new ReactiveProperty<bool>(config.Enabled);
        TimeoutDraft = new ReactiveProperty<string>(config.ExecutionTimeoutSeconds.ToString());
        _acceptedTimeoutText = TimeoutDraft.Value;
        SelectedRow = new ReactiveProperty<int>(0);
        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        IsSaved = new ReactiveProperty<bool>(false);
        RouteSummary = new ReactiveProperty<InboundWebhookRouteSummary>(ReadRouteSummary());
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<bool> Enabled { get; }
    public ReactiveProperty<string> TimeoutDraft { get; }
    public ReactiveProperty<int> SelectedRow { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<bool> IsSaved { get; }
    public ReactiveProperty<InboundWebhookRouteSummary> RouteSummary { get; }

    public IReadOnlyList<string> Rows { get; } =
    [
        "Enabled",
        "Execution timeout",
        "Route authoring"
    ];

    public void MoveSelection(int delta)
    {
        var next = Math.Clamp(SelectedRow.Value + delta, 0, Rows.Count - 1);
        if (next != SelectedRow.Value)
            SelectedRow.Value = next;
    }

    public void ToggleEnabled()
    {
        Enabled.Value = !Enabled.Value;
        IsSaved.Value = false;
        ClearStatus();
        RequestRedraw();
    }

    public void AppendTimeoutText(string text)
    {
        if (SelectedRow.Value != 1)
            return;

        if (TimeoutDraft.Value == _acceptedTimeoutText)
            TimeoutDraft.Value = string.Empty;

        TimeoutDraft.Value += text;
        IsSaved.Value = false;
        ClearStatus();
        RequestRedraw();
    }

    public void BackspaceTimeout()
    {
        if (SelectedRow.Value != 1 || TimeoutDraft.Value.Length == 0)
            return;

        TimeoutDraft.Value = TimeoutDraft.Value[..^1];
        IsSaved.Value = false;
        ClearStatus();
        RequestRedraw();
    }

    public bool Save()
    {
        RouteSummary.Value = ReadRouteSummary();
        if (!TryParseTimeout(TimeoutDraft.Value, out var timeoutSeconds, out var timeoutError))
        {
            Status.Value = new ConfigStatusMessage(timeoutError, ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        if (Enabled.Value && RouteSummary.Value.Enabled == 0)
        {
            Status.Value = new ConfigStatusMessage(
                "Inbound webhooks cannot be enabled until at least one valid route exists. Use `netclaw webhooks set` first.",
                ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        config["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        ConfigFileHelper.SetPathValue(config, "Webhooks.Enabled", Enabled.Value);
        ConfigFileHelper.SetPathValue(config, "Webhooks.ExecutionTimeoutSeconds", timeoutSeconds);
        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);

        _acceptedTimeoutText = timeoutSeconds.ToString();
        TimeoutDraft.Value = _acceptedTimeoutText;
        IsSaved.Value = true;
        Status.Value = new ConfigStatusMessage("Inbound Webhooks settings saved.", ConfigStatusTone.Success);
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
        Enabled.Dispose();
        TimeoutDraft.Dispose();
        SelectedRow.Dispose();
        Status.Dispose();
        IsSaved.Dispose();
        RouteSummary.Dispose();
        base.Dispose();
    }

    private WebhooksConfig LoadConfig()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        return new WebhooksConfig
        {
            Enabled = ConfigFileHelper.TryGetPathValue(config, "Webhooks.Enabled", out var enabled)
                && enabled is bool enabledFlag
                && enabledFlag,
            ExecutionTimeoutSeconds = ConfigFileHelper.TryGetPathValue(config, "Webhooks.ExecutionTimeoutSeconds", out var timeout)
                && TryConvertInt(timeout, out var timeoutValue)
                    ? timeoutValue
                    : 300
        };
    }

    private InboundWebhookRouteSummary ReadRouteSummary()
    {
        int total = 0, enabled = 0, disabled = 0, invalid = 0;
        foreach (var route in _routeStore.ListRouteFiles())
        {
            total++;
            if (route.Definition is null)
            {
                invalid++;
                continue;
            }

            var errors = WebhookRouteValidator.Validate(route.RouteName, route.Definition);
            if (errors.Count > 0)
            {
                invalid++;
                continue;
            }

            if (route.Definition.Enabled)
                enabled++;
            else
                disabled++;
        }

        return new InboundWebhookRouteSummary(total, enabled, disabled, invalid);
    }

    private static bool TryParseTimeout(string value, out int timeoutSeconds, out string error)
    {
        timeoutSeconds = 0;
        error = string.Empty;
        if (!int.TryParse(value.Trim(), out var parsed))
        {
            error = "Execution timeout must be a whole number of seconds.";
            return false;
        }

        if (parsed is < 1 or > 3600)
        {
            error = "Execution timeout must be between 1 and 3600 seconds.";
            return false;
        }

        timeoutSeconds = parsed;
        return true;
    }

    private static bool TryConvertInt(object? value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                result = (int)l;
                return true;
            case string text when int.TryParse(text, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private void ClearStatus()
    {
        if (!string.IsNullOrWhiteSpace(Status.Value.Text))
            Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
    }
}
