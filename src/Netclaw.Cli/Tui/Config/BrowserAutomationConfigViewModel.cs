// -----------------------------------------------------------------------
// <copyright file="BrowserAutomationConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

internal sealed record BrowserAutomationPrerequisiteStatus(
    bool CanEnable,
    string Summary,
    IReadOnlyList<string> MissingPrerequisites,
    IReadOnlyList<string> ManualInstallSteps);

internal interface IBrowserAutomationPrerequisiteProbe
{
    BrowserAutomationPrerequisiteStatus Detect(BrowserAutomationBackend backend);
}

internal sealed class BrowserAutomationPrerequisiteProbe : IBrowserAutomationPrerequisiteProbe
{
    public BrowserAutomationPrerequisiteStatus Detect(BrowserAutomationBackend backend)
    {
        var missing = new List<string>();
        var steps = new List<string>();

        if (!BrowserAutomationRuntimeDetector.HasNodeRuntime())
        {
            missing.Add("Node.js with npx");
            steps.Add("Install Node.js 20+ or run the Netclaw browser tooling installer outside this TUI.");
        }

        switch (backend)
        {
            case BrowserAutomationBackend.Playwright:
                var browser = BrowserAutomationRuntimeDetector.GetPreferredPlaywrightBrowser();
                if (!BrowserAutomationRuntimeDetector.HasPlaywrightBrowserRuntime(browser))
                {
                    missing.Add($"Playwright {browser} browser runtime");
                    steps.Add($"Install the browser runtime manually: npx -y playwright install {browser}");
                }
                break;
            case BrowserAutomationBackend.ChromeDevTools:
                var chrome = BrowserAutomationRuntimeDetector.DetectChrome();
                if (!chrome.IsInstalled)
                {
                    missing.Add("Chrome or Chromium");
                    steps.Add("Install Chrome/Chromium or set CHROME_PATH to the browser executable.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
        }

        return missing.Count == 0
            ? new BrowserAutomationPrerequisiteStatus(true, "Browser automation prerequisites are available.", [], steps)
            : new BrowserAutomationPrerequisiteStatus(false, "Browser automation prerequisites are missing.", missing, steps);
    }
}

internal sealed class BrowserAutomationConfigViewModel : ReactiveViewModel
{
    public const string PlaywrightServerName = "browser_playwright";
    public const string ChromeDevToolsServerName = "browser_chrome_devtools";

    private static readonly BrowserAutomationBackend[] Backends =
    [
        BrowserAutomationBackend.Playwright,
        BrowserAutomationBackend.ChromeDevTools
    ];

    private readonly NetclawPaths _paths;
    private readonly IBrowserAutomationPrerequisiteProbe _probe;

    public BrowserAutomationConfigViewModel(
        NetclawPaths paths,
        IBrowserAutomationPrerequisiteProbe? probe = null)
    {
        _paths = paths;
        _probe = probe ?? new BrowserAutomationPrerequisiteProbe();
        var state = LoadState(paths);
        Enabled = new ReactiveProperty<bool>(state.Enabled);
        SelectedBackendIndex = new ReactiveProperty<int>(Array.IndexOf(Backends, state.Backend) is var index && index >= 0 ? index : 0);
        SelectedRow = new ReactiveProperty<int>(0);
        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        IsSaved = new ReactiveProperty<bool>(false);
        Prerequisites = new ReactiveProperty<BrowserAutomationPrerequisiteStatus>(_probe.Detect(SelectedBackend));
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<bool> Enabled { get; }
    public ReactiveProperty<int> SelectedBackendIndex { get; }
    public ReactiveProperty<int> SelectedRow { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<bool> IsSaved { get; }
    internal ReactiveProperty<BrowserAutomationPrerequisiteStatus> Prerequisites { get; }

    public BrowserAutomationBackend SelectedBackend => Backends[SelectedBackendIndex.Value];
    public string SelectedBackendLabel => FormatBackend(SelectedBackend);
    public string SelectedCanonicalServerName => GetCanonicalServerName(SelectedBackend);

    public IReadOnlyList<string> Rows { get; } =
    [
        "Enabled",
        "Backend",
        "MCP permissions"
    ];

    public void MoveSelection(int delta)
    {
        var next = Math.Clamp(SelectedRow.Value + delta, 0, Rows.Count - 1);
        if (next != SelectedRow.Value)
            SelectedRow.Value = next;
    }

    public bool ToggleEnabled()
    {
        var previous = Enabled.Value;
        Enabled.Value = !Enabled.Value;
        if (AutosaveCompletedAction(
            Enabled.Value
                ? $"Browser Automation saved as {SelectedCanonicalServerName}. Use MCP permissions to grant access."
                : "Browser Automation disabled and canonical browser MCP profiles removed."))
        {
            return true;
        }

        Enabled.Value = previous;
        IsSaved.Value = false;
        RequestRedraw();
        return false;
    }

    public bool CycleBackend(int delta)
    {
        var previousIndex = SelectedBackendIndex.Value;
        var next = SelectedBackendIndex.Value + delta;
        if (next < 0)
            next = Backends.Length - 1;
        if (next >= Backends.Length)
            next = 0;

        SelectedBackendIndex.Value = next;
        Prerequisites.Value = _probe.Detect(SelectedBackend);
        if (AutosaveCompletedAction(
            Enabled.Value
                ? $"Browser Automation saved as {SelectedCanonicalServerName}. Use MCP permissions to grant access."
                : "Browser Automation backend preference updated; browser profiles remain disabled."))
        {
            return true;
        }

        SelectedBackendIndex.Value = previousIndex;
        Prerequisites.Value = _probe.Detect(SelectedBackend);
        IsSaved.Value = false;
        RequestRedraw();
        return false;
    }

    public void ActivateSelected()
    {
        switch (SelectedRow.Value)
        {
            case 0:
                ToggleEnabled();
                break;
            case 1:
                CycleBackend(1);
                break;
            case 2:
                OpenMcpPermissions();
                break;
        }
    }

    public bool Save()
        => Save(Enabled.Value
            ? $"Browser Automation saved as {SelectedCanonicalServerName}. Use MCP permissions to grant access."
            : "Browser Automation disabled and canonical browser MCP profiles removed.");

    private bool Save(string successMessage)
    {
        Prerequisites.Value = _probe.Detect(SelectedBackend);
        if (Enabled.Value && !Prerequisites.Value.CanEnable)
        {
            Status.Value = new ConfigStatusMessage(
                $"Cannot enable Browser Automation: {string.Join(", ", Prerequisites.Value.MissingPrerequisites)} missing.",
                ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        config["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        var servers = ConfigFileHelper.GetOrCreateSection(config, "McpServers");
        servers.Remove(PlaywrightServerName);
        servers.Remove(ChromeDevToolsServerName);

        if (Enabled.Value)
        {
            var (name, entry) = BrowserAutomationMcpProfiles.Create(SelectedBackend);
            servers[name] = ToDictionary(entry);
        }
        else if (servers.Count == 0)
        {
            config.Remove("McpServers");
        }

        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);
        IsSaved.Value = true;
        Status.Value = new ConfigStatusMessage(successMessage, ConfigStatusTone.Success);
        RequestRedraw();
        return true;
    }

    private bool AutosaveCompletedAction(string successMessage)
        => ConfigAutosave.Run(
            () => Save(successMessage),
            Status,
            "Browser Automation autosave failed",
            RequestRedraw);

    public void OpenMcpPermissions()
    {
        RouteRequested?.Invoke("/mcp-tools");
        Navigate?.Invoke("/mcp-tools");
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
        SelectedBackendIndex.Dispose();
        SelectedRow.Dispose();
        Status.Dispose();
        IsSaved.Dispose();
        Prerequisites.Dispose();
        base.Dispose();
    }

    private static (bool Enabled, BrowserAutomationBackend Backend) LoadState(NetclawPaths paths)
    {
        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var hasPlaywright = ConfigFileHelper.TryGetPathValue(config, $"McpServers.{PlaywrightServerName}", out var playwrightRaw);
        var hasChromeDevTools = ConfigFileHelper.TryGetPathValue(config, $"McpServers.{ChromeDevToolsServerName}", out var chromeRaw);

        if (hasPlaywright && playwrightRaw is not null)
            return (IsServerEnabled(playwrightRaw), BrowserAutomationBackend.Playwright);

        if (hasChromeDevTools && chromeRaw is not null)
            return (IsServerEnabled(chromeRaw), BrowserAutomationBackend.ChromeDevTools);

        return (false, BrowserAutomationBackend.Playwright);
    }

    private static bool IsServerEnabled(object? raw)
    {
        if (raw is Dictionary<string, object> dict
            && ConfigFileHelper.TryGetPathValue(dict, "Enabled", out var dictEnabled)
            && dictEnabled is bool dictFlag)
        {
            return dictFlag;
        }

        if (raw is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("Enabled", out var enabledProp)
                && enabledProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return enabledProp.GetBoolean();
            }

            return true;
        }

        return true;
    }

    private static Dictionary<string, object?> ToDictionary(McpServerEntry entry)
        => new()
        {
            ["Transport"] = entry.Transport,
            ["Command"] = entry.Command,
            ["Arguments"] = entry.Arguments,
            ["EnvironmentVariables"] = entry.EnvironmentVariables,
            ["Enabled"] = entry.Enabled,
            ["GrantCategory"] = entry.GrantCategory
        };

    private static string GetCanonicalServerName(BrowserAutomationBackend backend)
        => backend == BrowserAutomationBackend.ChromeDevTools ? ChromeDevToolsServerName : PlaywrightServerName;

    private static string FormatBackend(BrowserAutomationBackend backend)
        => backend switch
        {
            BrowserAutomationBackend.ChromeDevTools => "Chrome DevTools",
            _ => "Playwright"
        };

    private void ClearStatus()
    {
        if (!string.IsNullOrWhiteSpace(Status.Value.Text))
            Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
    }
}
