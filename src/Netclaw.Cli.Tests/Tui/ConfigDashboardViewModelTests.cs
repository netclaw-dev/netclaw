// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
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
    [InlineData("Browser Automation", "/browser-automation")]
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

    [Theory]
    [InlineData("Skill Sources")]
    [InlineData("Telemetry & Alerting")]
    public void Placeholder_sections_report_not_implemented_status(string label)
    {
        using var vm = new ConfigDashboardViewModel(new ConfigDashboardNavigationState());

        vm.Activate(vm.Items.Single(item => item.Label == label));

        Assert.Contains("not implemented yet", vm.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
    }
}
