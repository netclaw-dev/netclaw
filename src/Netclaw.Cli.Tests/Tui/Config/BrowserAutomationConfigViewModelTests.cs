// -----------------------------------------------------------------------
// <copyright file="BrowserAutomationConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class BrowserAutomationConfigViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public BrowserAutomationConfigViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Browser_automation_dashboard_entry_routes_to_real_editor()
    {
        using var vm = new Netclaw.Cli.Tui.ConfigDashboardViewModel(new Netclaw.Cli.Tui.ConfigDashboardNavigationState());
        string? route = null;
        vm.RouteRequested = r => route = r;

        vm.Activate(vm.Items.Single(static item => item.Label == "Browser Automation"));

        Assert.Equal("/browser-automation", route);
    }

    [Fact]
    public void Save_refuses_enablement_when_prerequisites_are_missing()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new BrowserAutomationConfigViewModel(_paths, new FakeProbe(false));

        Assert.False(vm.ToggleEnabled());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("missing", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.False(vm.Enabled.Value);
    }

    [Fact]
    public void Save_persists_playwright_canonical_mcp_profile_for_runtime_binding()
    {
        using var vm = new BrowserAutomationConfigViewModel(_paths, new FakeProbe(true));

        vm.ToggleEnabled();

        Assert.True(vm.Save());

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "McpServers.browser_playwright.Transport", out var transport));
        Assert.Equal("stdio", transport);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "McpServers.browser_playwright.GrantCategory", out var grant));
        Assert.Equal("browser_automation", grant);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "McpServers.browser_playwright.Enabled", out var enabled));
        Assert.Equal(true, enabled);
        Assert.False(ConfigFileHelper.TryGetPathValue(config, "McpServers.browser_chrome_devtools", out _));

        var bound = BindMcpServers();
        Assert.True(bound.TryGetValue("browser_playwright", out var entry));
        Assert.Equal("stdio", entry.Transport);
        Assert.Equal("browser_automation", entry.GrantCategory);
    }

    [Fact]
    public void Switching_backend_removes_inactive_canonical_profile()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"McpServers\":{\"browser_playwright\":{\"Transport\":\"stdio\",\"Command\":\"npx\",\"Enabled\":true}}}");
        using var vm = new BrowserAutomationConfigViewModel(_paths, new FakeProbe(true));

        vm.CycleBackend(1);

        Assert.True(vm.Save());
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.False(ConfigFileHelper.TryGetPathValue(config, "McpServers.browser_playwright", out _));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "McpServers.browser_chrome_devtools.Transport", out var transport));
        Assert.Equal("stdio", transport);
    }

    [Fact]
    public void Disable_removes_only_canonical_browser_profiles()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"McpServers\":{\"browser_playwright\":{\"Transport\":\"stdio\",\"Enabled\":true},\"memorizer\":{\"Transport\":\"stdio\",\"Command\":\"uvx\",\"Enabled\":true}}}");
        using var vm = new BrowserAutomationConfigViewModel(_paths, new FakeProbe(true));

        vm.ToggleEnabled();

        Assert.True(vm.Save());
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.False(ConfigFileHelper.TryGetPathValue(config, "McpServers.browser_playwright", out _));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "McpServers.memorizer.Transport", out var transport));
        Assert.Equal("stdio", transport);
    }

    [Fact]
    public void Mcp_permissions_route_is_forwarded_without_raw_grant_editing()
    {
        using var vm = new BrowserAutomationConfigViewModel(_paths, new FakeProbe(true));
        string? route = null;
        vm.RouteRequested = r => route = r;

        vm.OpenMcpPermissions();

        Assert.Equal("/mcp-tools", route);
    }

    private Dictionary<string, McpServerEntry> BindMcpServers()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(_paths.NetclawConfigPath)
            .Build();
        return configuration.GetSection("McpServers").Get<Dictionary<string, McpServerEntry>>()!;
    }

    private sealed class FakeProbe(bool canEnable) : IBrowserAutomationPrerequisiteProbe
    {
        public BrowserAutomationPrerequisiteStatus Detect(BrowserAutomationBackend backend)
            => canEnable
                ? new BrowserAutomationPrerequisiteStatus(true, "ok", [], [])
                : new BrowserAutomationPrerequisiteStatus(false, "missing", ["Node.js with npx"], ["Install Node.js manually."]);
    }
}
