// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

public enum ConfigDashboardAction
{
    None,
    RunDoctor,
}

public sealed class ConfigDashboardNavigationState
{
    public ConfigDashboardAction PendingAction { get; set; }
}

public sealed record ConfigDashboardItem(string Label, string Description, string? Route = null, bool IsTerminal = false);

/// <summary>
/// Root dashboard for <c>netclaw config</c>. Provider and model management are
/// routed into their dedicated TUIs; the remaining areas are scaffolded as
/// domain-oriented entries so config no longer lands on a stub.
/// </summary>
public sealed class ConfigDashboardViewModel : ReactiveViewModel
{
    private readonly ConfigDashboardNavigationState _navigationState;

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ConfigDashboardViewModel(ConfigDashboardNavigationState navigationState)
    {
        _navigationState = navigationState;
    }

    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public ReactiveProperty<int> SelectedIndex { get; } = new(0);

    public IReadOnlyList<ConfigDashboardItem> Items { get; } =
    [
        new("Inference Providers", "Manage provider definitions and authentication.", "/provider"),
        new("Models", "Assign model roles and discover provider models.", "/model"),
        new("Channels", "Slack, Discord, and Mattermost settings.", "/channels"),
        new("Inbound Webhooks", "Global webhook enablement and route diagnostics.", "/inbound-webhooks"),
        new("Skill Sources", "External skills and private skill feeds.", "/skill-sources"),
        new("Search", "Search backend and credentials.", "/search"),
        new("Browser Automation", "Canonical browser MCP profile settings.", "/browser-automation"),
        new("Telemetry & Alerting", "Telemetry and outbound webhook alerting.", "/telemetry-alerting"),
        new("Security & Access", "Posture, enabled features, audience profiles, and exposure mode.", "/security"),
        new("Workspaces Directory", "Project discovery root for workspace-aware prompts.", "/workspaces"),
        new("Run Full Doctor", "Exit the dashboard and run `netclaw doctor`.", IsTerminal: true),
        new("Quit", "Exit without changing settings.", IsTerminal: true),
    ];

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
            return;

        var next = Math.Clamp(SelectedIndex.Value + delta, 0, Items.Count - 1);
        if (next != SelectedIndex.Value)
            SelectedIndex.Value = next;
    }

    public void ActivateSelected()
    {
        Activate(Items[SelectedIndex.Value]);
    }

    internal void Activate(ConfigDashboardItem item)
    {
        if (item.Route is not null)
        {
            RouteRequested?.Invoke(item.Route);
            Navigate?.Invoke(item.Route);
            return;
        }

        if (string.Equals(item.Label, "Run Full Doctor", StringComparison.Ordinal))
        {
            _navigationState.PendingAction = ConfigDashboardAction.RunDoctor;
            ShutdownRequestedForTest = true;
            Shutdown();
            return;
        }

        if (string.Equals(item.Label, "Quit", StringComparison.Ordinal))
        {
            ShutdownRequestedForTest = true;
            Shutdown();
            return;
        }

        StatusMessage.Value = $"{item.Label} is not implemented yet in `netclaw config`.";
        RequestRedraw();
    }

    public void RequestQuit() => Shutdown();

    public override void Dispose()
    {
        StatusMessage.Dispose();
        SelectedIndex.Dispose();
        base.Dispose();
    }
}
