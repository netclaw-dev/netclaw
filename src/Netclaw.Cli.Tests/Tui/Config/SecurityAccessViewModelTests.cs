// -----------------------------------------------------------------------
// <copyright file="SecurityAccessViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Config;
using Netclaw.Cli.Tests.Tui.Wizard;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class SecurityAccessViewModelTests : WizardStepTestBase
{
    [Fact]
    public void Security_access_lists_expected_leaf_entries()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);

        var labels = vm.Items.Select(static item => item.Label).ToArray();

        Assert.Equal(
        [
            "Security Posture",
            "Enabled Features",
            "Audience Profiles",
            "Exposure Mode"
        ], labels);
    }

    [Fact]
    public void Exposure_mode_routes_to_exposure_editor()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);
        string? route = null;
        vm.RouteRequested = value => route = value;

        vm.Activate(vm.Items.Single(static item => item.Label == "Exposure Mode"));

        Assert.Equal("/exposure-mode", route);
    }

    [Fact]
    public void Exposure_summary_reads_existing_daemon_mode()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "cloudflare-tunnel" }
            }
            """);

        using var vm = new SecurityAccessViewModel(Context.Paths);

        var exposure = vm.Items.Single(static item => item.Label == "Exposure Mode");
        Assert.Equal("Cloudflare Tunnel", exposure.Summary);
    }
}
