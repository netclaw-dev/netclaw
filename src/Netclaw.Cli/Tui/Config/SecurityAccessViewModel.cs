// -----------------------------------------------------------------------
// <copyright file="SecurityAccessViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

public sealed record SecurityAccessItem(string Label, string Summary, string Description, string? Route = null);

public sealed class SecurityAccessViewModel : ReactiveViewModel
{
    private const int FeatureCount = 6;
    private static readonly string[] FeatureConfigPaths =
    [
        "Memory.Enabled",
        "Search.Enabled",
        "SkillSync.Enabled",
        "Scheduling.Enabled",
        "SubAgents.Enabled",
        "Webhooks.Enabled"
    ];

    private readonly NetclawPaths _paths;
    private readonly bool[] _enabledFeatures = new bool[FeatureCount];

    public SecurityAccessViewModel(NetclawPaths paths)
    {
        _paths = paths;
        LoadEnabledFeatures();
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public ReactiveProperty<int> SelectedIndex { get; } = new(0);
    public ReactiveProperty<bool> EditingEnabledFeatures { get; } = new(false);
    public ReactiveProperty<int> SelectedFeatureIndex { get; } = new(0);

    public IReadOnlyList<SecurityAccessItem> Items => BuildItems();
    public IReadOnlyList<string> FeatureNames => FeatureSelectionStepViewModel.FeatureNames;
    public IReadOnlyList<string> FeatureDescriptions => FeatureSelectionStepViewModel.FeatureDescriptions;

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
        if (EditingEnabledFeatures.Value)
        {
            ToggleSelectedFeature();
            return;
        }

        var items = Items;
        if (items.Count == 0)
            return;

        Activate(items[SelectedIndex.Value]);
    }

    internal void Activate(SecurityAccessItem item)
    {
        if (item.Label == "Enabled Features")
        {
            EditingEnabledFeatures.Value = true;
            StatusMessage.Value = "";
            RequestRedraw();
            return;
        }

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
        if (EditingEnabledFeatures.Value)
        {
            EditingEnabledFeatures.Value = false;
            StatusMessage.Value = "";
            RequestRedraw();
            return;
        }

        RouteRequested?.Invoke("/config");
        Navigate?.Invoke("/config");
    }

    public void MoveFeatureSelection(int delta)
    {
        var next = Math.Clamp(SelectedFeatureIndex.Value + delta, 0, FeatureCount - 1);
        if (next != SelectedFeatureIndex.Value)
            SelectedFeatureIndex.Value = next;
    }

    public bool IsFeatureEnabled(int index) => _enabledFeatures[index];

    public void ToggleSelectedFeature()
    {
        var index = SelectedFeatureIndex.Value;
        _enabledFeatures[index] = !_enabledFeatures[index];

        var session = new ConfigEditorSession(_paths);
        session.Apply(BuildFeatureContribution());
        session.Save();

        var state = _enabledFeatures[index] ? "enabled" : "disabled";
        StatusMessage.Value = $"{FeatureNames[index]} {state}. Saved.";
        RequestRedraw();
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
        EditingEnabledFeatures.Dispose();
        SelectedFeatureIndex.Dispose();
        base.Dispose();
    }

    private void LoadEnabledFeatures()
    {
        Array.Fill(_enabledFeatures, true);
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        for (var i = 0; i < FeatureConfigPaths.Length; i++)
        {
            if (ConfigFileHelper.TryGetPathValue(config, FeatureConfigPaths[i], out var value) && value is bool enabled)
                _enabledFeatures[i] = enabled;
        }
    }

    private SectionContribution BuildFeatureContribution()
        => new(
        [
            new SectionFieldAction(FeatureConfigPaths[0], SectionFieldActionKind.Set, _enabledFeatures[0]),
            new SectionFieldAction(FeatureConfigPaths[1], SectionFieldActionKind.Set, _enabledFeatures[1]),
            new SectionFieldAction(FeatureConfigPaths[2], SectionFieldActionKind.Set, _enabledFeatures[2]),
            new SectionFieldAction(FeatureConfigPaths[3], SectionFieldActionKind.Set, _enabledFeatures[3]),
            new SectionFieldAction(FeatureConfigPaths[4], SectionFieldActionKind.Set, _enabledFeatures[4]),
            new SectionFieldAction(FeatureConfigPaths[5], SectionFieldActionKind.Set, _enabledFeatures[5])
        ]);

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
        var enabled = 0;
        foreach (var path in FeatureConfigPaths)
        {
            var flag = true;
            if (ConfigFileHelper.TryGetPathValue(config, path, out var value) && value is bool configuredFlag)
                flag = configuredFlag;

            if (flag)
                enabled++;
        }

        return $"{enabled}/{FeatureConfigPaths.Length} enabled";
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
