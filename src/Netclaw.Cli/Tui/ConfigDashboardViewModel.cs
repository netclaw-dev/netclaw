// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Channels;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
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

/// <summary>
/// Marker service registered only by the embedded <c>netclaw config</c> host. Its presence in DI
/// tells the routed Provider/Model managers they were reached from the config dashboard, so backing
/// out past their root navigates back to the dashboard instead of exiting the process. The
/// standalone <c>netclaw provider</c>/<c>netclaw model</c> hosts do not register it, leaving those
/// managers in their default "exit on back-out" behavior.
/// </summary>
public sealed class EmbeddedConfigHostMarker
{
}

public sealed record ConfigDashboardItem(string Label, string Description, string? Route = null, bool IsTerminal = false);

/// <summary>
/// Root dashboard for <c>netclaw config</c>. Provider and model management are
/// routed into their dedicated TUIs; the remaining areas are scaffolded as
/// domain-oriented entries so config no longer lands on a stub.
/// </summary>
/// <remarks>
/// Each row carries a live status summary computed from the current config on
/// disk (e.g. <c>Search  ✓ Brave</c>, <c>Security &amp; Access  Team · 4/6
/// enabled</c>). Statuses are read fresh whenever they are requested so edits
/// made in the sub-editors are reflected on return (autosave reentrancy). The
/// focused row's description renders as a dim help line below the list.
/// </remarks>
public sealed class ConfigDashboardViewModel : ReactiveViewModel
{
    private readonly ConfigDashboardNavigationState _navigationState;
    private readonly ConfigDashboardStatusReader? _statusReader;

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ConfigDashboardViewModel(ConfigDashboardNavigationState navigationState, NetclawPaths? paths = null)
    {
        _navigationState = navigationState;
        _statusReader = paths is null ? null : new ConfigDashboardStatusReader(paths);
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

    /// <summary>
    /// Computes the live status-summary column entry for an item. Terminal rows
    /// (Doctor / Quit) have no status. Returns an empty string when no config
    /// reader is available (e.g. unit tests constructing the VM directly).
    /// </summary>
    public string StatusFor(ConfigDashboardItem item)
    {
        if (item.IsTerminal || _statusReader is null)
            return string.Empty;

        return _statusReader.Summarize(item.Label);
    }

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

/// <summary>
/// Reads the live <c>netclaw.json</c> (and secrets) and renders a one-line
/// status summary for each dashboard area. Kept beside the view model because
/// it exists only to feed the dashboard's status column, and reads the same
/// section keys the dedicated editors write through their persistence seams.
/// </summary>
internal sealed class ConfigDashboardStatusReader
{
    private static readonly string[] FeatureConfigPaths =
    [
        "Memory.Enabled",
        "Search.Enabled",
        "SkillSync.Enabled",
        "Scheduling.Enabled",
        "SubAgents.Enabled",
        "Webhooks.Enabled"
    ];

    private static readonly (ChannelType Type, string Section)[] ChannelAdapters =
    [
        (ChannelType.Slack, "Slack"),
        (ChannelType.Discord, "Discord"),
        (ChannelType.Mattermost, "Mattermost")
    ];

    private readonly NetclawPaths _paths;

    internal ConfigDashboardStatusReader(NetclawPaths paths)
    {
        _paths = paths;
    }

    internal string Summarize(string label)
    {
        // Read once per call so edits made in sub-editors are reflected on
        // return to the dashboard (no caching = no staleness).
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        return label switch
        {
            "Inference Providers" => ProvidersSummary(config),
            "Models" => ModelsSummary(config),
            "Channels" => ChannelsSummary(config),
            "Inbound Webhooks" => OnOff(BoolAt(config, "Webhooks.Enabled")),
            "Skill Sources" => SkillSourcesSummary(config),
            "Search" => SearchSummary(config),
            "Browser Automation" => OnOff(BrowserEnabled(config)),
            "Telemetry & Alerting" => TelemetrySummary(config),
            "Security & Access" => SecuritySummary(config),
            "Workspaces Directory" => WorkspacesSummary(config),
            _ => string.Empty
        };
    }

    private static string ProvidersSummary(Dictionary<string, object> config)
    {
        var count = ConfigFileHelper.GetSectionOrNull(config, "Providers")?.Count ?? 0;
        return $"{count} configured";
    }

    private static string ModelsSummary(Dictionary<string, object> config)
    {
        if (ConfigFileHelper.TryGetPathValue(config, "Models.Main.ModelId", out var modelId)
            && modelId is string id && !string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        return "– not set";
    }

    private string ChannelsSummary(Dictionary<string, object> config)
    {
        var configured = new List<string>();
        var totalChannels = 0;
        foreach (var (_, section) in ChannelAdapters)
        {
            if (!BoolAt(config, $"{section}.Enabled"))
                continue;

            configured.Add(section);
            if (ConfigFileHelper.TryGetPathValue(config, $"{section}.AllowedChannelIds", out var raw)
                && raw is object[] channels)
            {
                totalChannels += channels.Length;
            }
        }

        if (configured.Count == 0)
            return "– none configured";

        if (configured.Count == 1)
            return $"{configured[0]} · {Pluralize(totalChannels, "channel", "channels")}";

        return $"{string.Join(" · ", configured)} · {Pluralize(totalChannels, "channel", "channels")}";
    }

    private string SkillSourcesSummary(Dictionary<string, object> config)
    {
        try
        {
            var dirs = ConfigFileHelper.LoadSection<ExternalSkillsConfig>(config, "ExternalSkills").Sources.Count;
            var feeds = ConfigFileHelper.LoadSection<SkillFeedsConfig>(config, "SkillFeeds").Feeds.Count;
            return $"{dirs} {(dirs == 1 ? "dir" : "dirs")} · {feeds} {(feeds == 1 ? "feed" : "feeds")}";
        }
        catch (JsonException)
        {
            // These two summaries deserialize whole sections (unlike the others, which use
            // TryGetPathValue and can't throw). A hand-edited/migrated section with the wrong shape
            // must degrade to a visible indicator here — Summarize runs in the dashboard layout render.
            return "– config error";
        }
    }

    private static string SearchSummary(Dictionary<string, object> config)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, "Search.Backend", out var raw)
            || raw is not string backend || string.IsNullOrWhiteSpace(backend))
        {
            return "– not set";
        }

        return backend.ToLowerInvariant() switch
        {
            "brave" => "✓ Brave",
            "searxng" => "✓ SearXNG",
            "duckduckgo" => "✓ DuckDuckGo",
            _ => $"✓ {backend}"
        };
    }

    private string TelemetrySummary(Dictionary<string, object> config)
    {
        var otlp = BoolAt(config, "Telemetry.Enabled") ? "on" : "off";
        try
        {
            var webhooks = ConfigFileHelper.LoadSection<NotificationsConfig>(config, "Notifications").Webhooks.Count;
            return $"OTLP {otlp} · {Pluralize(webhooks, "webhook", "webhooks")}";
        }
        catch (JsonException)
        {
            // A malformed Notifications section must not crash the dashboard layout render.
            return $"OTLP {otlp} · – config error";
        }
    }

    private static string SecuritySummary(Dictionary<string, object> config)
    {
        var posture = ConfigFileHelper.TryGetPathValue(config, "Security.DeploymentPosture", out var value)
            && value is string text
            && Enum.TryParse<DeploymentPosture>(text, ignoreCase: true, out var parsed)
                ? parsed
                : DeploymentPosture.Personal;

        var enabled = 0;
        foreach (var path in FeatureConfigPaths)
        {
            // Features default to enabled when absent, matching the security editor.
            var flag = true;
            if (ConfigFileHelper.TryGetPathValue(config, path, out var featureValue) && featureValue is bool configuredFlag)
                flag = configuredFlag;
            if (flag)
                enabled++;
        }

        return $"{posture} · {enabled}/{FeatureConfigPaths.Length} enabled";
    }

    private string WorkspacesSummary(Dictionary<string, object> config)
        => ConfigFileHelper.TryGetPathValue(config, "Workspaces.Directory", out var value)
            && value is string dir && !string.IsNullOrWhiteSpace(dir)
                ? dir
                : _paths.WorkspacesDirectory;

    private static bool BoolAt(Dictionary<string, object> config, string path)
        => ConfigFileHelper.TryGetPathValue(config, path, out var value) && value is bool flag && flag;

    // Browser Automation has no `Browser.Enabled` flag; the editor persists enablement as
    // the presence of the canonical browser MCP server entries, so the dashboard reads it
    // back the same way BrowserAutomationConfigViewModel does.
    private static bool BrowserEnabled(Dictionary<string, object> config)
        => ConfigFileHelper.TryGetPathValue(config, "McpServers.browser_playwright", out _)
            || ConfigFileHelper.TryGetPathValue(config, "McpServers.browser_chrome_devtools", out _);

    private static string OnOff(bool value) => value ? "enabled" : "– disabled";

    private static string Pluralize(int count, string singular, string plural)
        => $"{count} {(count == 1 ? singular : plural)}";
}
