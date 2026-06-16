// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ConfigDashboardViewModelTests
{
    [Fact]
    public void Root_dashboard_contains_expected_domain_entries()
    {
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState());

        var labels = vm.Items.Select(static item => item.Label).ToList();

        Assert.Equal(
        [
            "Inference Providers",
            "Models",
            "Channels",
            "Inbound Webhooks",
            "Skill Sources",
            "Search",
            "Browser Automation",
            "Telemetry & Alerting",
            "Security & Access",
            "Workspaces Directory",
            "Run Full Doctor",
            "Quit",
        ], labels);
    }

    [Fact]
    public void Inference_providers_routes_to_provider_page()
    {
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState());
        string? navigatedRoute = null;
        vm.RouteRequested = route => navigatedRoute = route;

        vm.Activate(vm.Items.Single(static item => item.Label == "Inference Providers"));

        Assert.Equal("/provider", navigatedRoute);
    }

    [Fact]
    public void Models_routes_to_model_page()
    {
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState());
        string? navigatedRoute = null;
        vm.RouteRequested = route => navigatedRoute = route;

        vm.Activate(vm.Items.Single(static item => item.Label == "Models"));

        Assert.Equal("/model", navigatedRoute);
    }

    [Fact]
    public void Security_access_routes_to_security_page()
    {
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState());
        string? navigatedRoute = null;
        vm.RouteRequested = route => navigatedRoute = route;

        vm.Activate(vm.Items.Single(static item => item.Label == "Security & Access"));

        Assert.Equal("/security", navigatedRoute);
    }

    [Fact]
    public void Channels_routes_to_channels_page()
    {
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState());
        string? navigatedRoute = null;
        vm.RouteRequested = route => navigatedRoute = route;

        vm.Activate(vm.Items.Single(static item => item.Label == "Channels"));

        Assert.Equal("/channels", navigatedRoute);
    }

    [Theory]
    [InlineData("Inbound Webhooks", "/inbound-webhooks")]
    [InlineData("Skill Sources", "/skill-sources")]
    [InlineData("Browser Automation", "/browser-automation")]
    [InlineData("Telemetry & Alerting", "/telemetry-alerting")]
    [InlineData("Workspaces Directory", "/workspaces")]
    public void Task1_config_areas_route_to_dedicated_pages(string label, string expectedRoute)
    {
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState());
        string? navigatedRoute = null;
        vm.RouteRequested = route => navigatedRoute = route;

        vm.Activate(vm.Items.Single(item => item.Label == label));

        Assert.Equal(expectedRoute, navigatedRoute);
    }

    [Fact]
    public void Run_full_doctor_sets_pending_action_and_shuts_down()
    {
        var navigationState = new ConfigDashboardNavigationState();
        using var vm = new ConfigDashboardViewModel(navigationState);

        vm.Activate(vm.Items.Single(static item => item.Label == "Run Full Doctor"));

        Assert.Equal(ConfigDashboardAction.RunDoctor, navigationState.PendingAction);
        Assert.True(vm.ShutdownRequestedForTest);
    }

    [Fact]
    public void Status_summary_is_empty_without_a_config_reader()
    {
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState());

        foreach (var item in vm.Items)
            Assert.Equal(string.Empty, vm.StatusFor(item));
    }

    [Fact]
    public void Terminal_rows_never_carry_a_status_summary()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState(), paths);

        Assert.Equal(string.Empty, vm.StatusFor(vm.Items.Single(i => i.Label == "Run Full Doctor")));
        Assert.Equal(string.Empty, vm.StatusFor(vm.Items.Single(i => i.Label == "Quit")));
    }

    [Fact]
    public void Malformed_sections_render_a_config_error_indicator_without_crashing()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        // Sources/Webhooks have the wrong JSON shape (a number / string instead of an array), as a
        // hand-edited or migrated config might. Summarize runs in the dashboard layout render and
        // must degrade to a visible indicator rather than throwing JsonException into the render loop.
        File.WriteAllText(paths.NetclawConfigPath,
            "{\"configVersion\":1,\"ExternalSkills\":{\"Sources\":42},\"Notifications\":{\"Webhooks\":\"nope\"}}");
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState(), paths);

        Assert.Contains("config error", vm.StatusFor(vm.Items.Single(i => i.Label == "Skill Sources")), StringComparison.Ordinal);
        Assert.Contains("config error", vm.StatusFor(vm.Items.Single(i => i.Label == "Telemetry & Alerting")), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_summaries_reflect_an_empty_default_config()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState(), paths);

        Assert.Equal("0 configured", Summary(vm, "Inference Providers"));
        Assert.Equal("– not set", Summary(vm, "Models"));
        Assert.Equal("– none configured", Summary(vm, "Channels"));
        Assert.Equal("– disabled", Summary(vm, "Inbound Webhooks"));
        Assert.Equal("0 dirs · 0 feeds", Summary(vm, "Skill Sources"));
        Assert.Equal("– not set", Summary(vm, "Search"));
        Assert.Equal("– disabled", Summary(vm, "Browser Automation"));
        Assert.Equal("OTLP off · 0 webhooks", Summary(vm, "Telemetry & Alerting"));
        // Features default to enabled when absent, so a bare config reports 6/6.
        Assert.Equal("Personal · 6/6 enabled", Summary(vm, "Security & Access"));
        Assert.Equal(paths.WorkspacesDirectory, Summary(vm, "Workspaces Directory"));
    }

    [Fact]
    public void Status_summaries_reflect_a_populated_config()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Providers": { "anthropic": { "Type": "anthropic" }, "openai": { "Type": "openai" } },
              "Models": { "Main": { "Provider": "anthropic", "ModelId": "claude-opus-4" } },
              "Slack": { "Enabled": true, "AllowedChannelIds": ["C01", "C02"] },
              "Discord": { "Enabled": true, "AllowedChannelIds": ["123"] },
              "Webhooks": { "Enabled": true },
              "ExternalSkills": { "Sources": [ { "Name": "claude-code" } ] },
              "SkillFeeds": { "Feeds": [ { "Name": "corp", "Url": "https://skills.corp.com" } ] },
              "Search": { "Backend": "brave" },
              "McpServers": { "browser_playwright": { "Command": "npx", "Args": ["@playwright/mcp@latest"] } },
              "Telemetry": { "Enabled": true },
              "Notifications": { "Webhooks": [ { "Url": "https://hooks.slack.com/x" } ] },
              "Security": { "DeploymentPosture": "Team", "Memory": { "Enabled": false } },
              "Memory": { "Enabled": false }
            }
            """);
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState(), paths);

        Assert.Equal("2 configured", Summary(vm, "Inference Providers"));
        Assert.Equal("claude-opus-4", Summary(vm, "Models"));
        Assert.Equal("Slack · Discord · 3 channels", Summary(vm, "Channels"));
        Assert.Equal("enabled", Summary(vm, "Inbound Webhooks"));
        Assert.Equal("1 dir · 1 feed", Summary(vm, "Skill Sources"));
        Assert.Equal("✓ Brave", Summary(vm, "Search"));
        Assert.Equal("enabled", Summary(vm, "Browser Automation"));
        Assert.Equal("OTLP on · 1 webhook", Summary(vm, "Telemetry & Alerting"));
        // Memory.Enabled=false drops the count to 5/6.
        Assert.Equal("Team · 5/6 enabled", Summary(vm, "Security & Access"));
    }

    [Fact]
    public void Status_summaries_are_recomputed_on_each_read_for_autosave_reentrancy()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, "{ \"configVersion\": 1, \"Search\": { \"Backend\": \"duckduckgo\" } }");
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState(), paths);

        Assert.Equal("✓ DuckDuckGo", Summary(vm, "Search"));

        // Simulate a sub-editor autosave changing the backend, then returning.
        File.WriteAllText(paths.NetclawConfigPath, "{ \"configVersion\": 1, \"Search\": { \"Backend\": \"brave\" } }");

        Assert.Equal("✓ Brave", Summary(vm, "Search"));
    }

    private static string Summary(ConfigDashboardViewModel vm, string label)
        => vm.StatusFor(vm.Items.Single(item => item.Label == label));
}
