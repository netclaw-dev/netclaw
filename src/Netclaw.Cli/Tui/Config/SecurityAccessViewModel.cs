// -----------------------------------------------------------------------
// <copyright file="SecurityAccessViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

public sealed record SecurityAccessItem(string Label, string Summary, string Description, string? Route = null);

public sealed class SecurityAccessViewModel : ReactiveViewModel
{
    private readonly NetclawPaths _paths;

    public SecurityAccessViewModel(NetclawPaths paths)
    {
        _paths = paths;
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public ReactiveProperty<int> SelectedIndex { get; } = new(0);

    public IReadOnlyList<SecurityAccessItem> Items => BuildItems();

    public void MoveSelection(int delta)
    {
        var items = Items;
        if (items.Count == 0)
            return;

        var next = Math.Clamp(SelectedIndex.Value + delta, 0, items.Count - 1);
        if (next != SelectedIndex.Value)
            SelectedIndex.Value = next;
    }

    public void ActivateSelected()
    {
        var items = Items;
        if (items.Count == 0)
            return;

        Activate(items[SelectedIndex.Value]);
    }

    internal void Activate(SecurityAccessItem item)
    {
        if (item.Route is not null)
        {
            RouteRequested?.Invoke(item.Route);
            Navigate?.Invoke(item.Route);
            return;
        }

        StatusMessage.Value = $"{item.Label} is not implemented yet in `netclaw config`.";
        RequestRedraw();
    }

    public void BackToConfig()
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
        StatusMessage.Dispose();
        SelectedIndex.Dispose();
        base.Dispose();
    }

    private IReadOnlyList<SecurityAccessItem> BuildItems()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        return
        [
            new("Security Posture", ReadPostureSummary(config), "Deployment trust stance."),
            new("Enabled Features", ReadEnabledFeaturesSummary(config), "Deployment-wide runtime feature gates."),
            new("Audience Profiles", "Not implemented", "Curated per-audience access rules."),
            new("Exposure Mode", ReadExposureModeSummary(config), "Daemon reachability and tunnel topology.", "/exposure-mode")
        ];
    }

    private static string ReadPostureSummary(Dictionary<string, object> config)
    {
        if (ConfigFileHelper.TryGetPathValue(config, "Security.DeploymentPosture", out var value)
            && value is string posture
            && !string.IsNullOrWhiteSpace(posture))
        {
            return posture;
        }

        return "Personal";
    }

    private static string ReadEnabledFeaturesSummary(Dictionary<string, object> config)
    {
        var paths = new[]
        {
            "Memory.Enabled",
            "Search.Enabled",
            "SkillSync.Enabled",
            "Scheduling.Enabled",
            "SubAgents.Enabled",
            "Webhooks.Enabled"
        };

        var configured = 0;
        var enabled = 0;
        foreach (var path in paths)
        {
            if (!ConfigFileHelper.TryGetPathValue(config, path, out var value) || value is not bool flag)
                continue;

            configured++;
            if (flag)
                enabled++;
        }

        return configured == 0 ? "Defaults" : $"{enabled}/{paths.Length} enabled";
    }

    private static string ReadExposureModeSummary(Dictionary<string, object> config)
    {
        var mode = ExposureMode.Local;
        if (ConfigFileHelper.TryGetPathValue(config, "Daemon.ExposureMode", out var value))
            mode = DaemonConfig.ParseExposureMode(value?.ToString());

        return mode switch
        {
            ExposureMode.Local => "Local",
            ExposureMode.ReverseProxy => "Reverse Proxy",
            ExposureMode.TailscaleServe => "Tailscale Serve",
            ExposureMode.TailscaleFunnel => "Tailscale Funnel",
            ExposureMode.CloudflareTunnel => "Cloudflare Tunnel",
            _ => mode.ToString()
        };
    }
}
