// -----------------------------------------------------------------------
// <copyright file="ExposureModeConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Tests.Tui.Wizard;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class ExposureModeConfigViewModelTests : WizardStepTestBase
{
    [Fact]
    public void Constructor_prefills_existing_exposure_mode()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "reverse-proxy",
                "Host": "10.0.0.5",
                "TrustedProxies": ["10.0.0.0/24"]
              }
            }
            """);

        using var vm = new ExposureModeConfigViewModel(Context.Paths);

        Assert.Equal(ExposureMode.ReverseProxy, vm.Step.SelectedMode);
        Assert.Equal("10.0.0.5", vm.Step.Host);
        Assert.Equal(["10.0.0.0/24"], vm.Step.TrustedProxies);
    }

    [Fact]
    public void Saving_tunnel_mode_preserves_unrelated_daemon_fields()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "local",
                "Host": "127.0.0.1",
                "Port": 5299,
                "DisableSelfUpdate": true
              }
            }
            """);

        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.TailscaleServe;

        vm.GoNext();
        vm.GoNext();

        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.ExposureMode", out var mode));
        Assert.Equal("tailscale-serve", mode);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.Port", out var port));
        Assert.Equal(5299L, port);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.DisableSelfUpdate", out var disableSelfUpdate));
        Assert.Equal(true, disableSelfUpdate);
        Assert.False(ConfigFileHelper.TryGetPathValue(config, "Daemon.Host", out _));
        Assert.True(vm.IsSaved.Value);
    }

    [Fact]
    public void Saving_reverse_proxy_writes_mode_specific_fields()
    {
        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.ReverseProxy;
        vm.Step.Host = "10.0.0.5";
        vm.Step.TrustedProxies = ["10.0.0.0/24"];

        vm.GoNext();
        vm.GoNext();
        vm.GoNext();
        vm.GoNext();

        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.ExposureMode", out var mode));
        Assert.Equal("reverse-proxy", mode);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.Host", out var host));
        Assert.Equal("10.0.0.5", host);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.TrustedProxies", out var proxies));
        Assert.Equal(["10.0.0.0/24"], Assert.IsType<object[]>(proxies).Select(static item => item.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public void Saving_local_mode_preserves_reverse_proxy_values_for_reactivation()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "reverse-proxy",
                "Host": "10.0.0.5",
                "Port": 5299,
                "DisableSelfUpdate": true,
                "TrustedProxies": ["10.0.0.0/24"]
              }
            }
            """);

        using (var vm = new ExposureModeConfigViewModel(Context.Paths))
        {
            vm.Step.SelectedMode = ExposureMode.Local;
            vm.GoNext();
        }

        var localConfig = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(localConfig, "Daemon.ExposureMode", out var localMode));
        Assert.Equal("local", localMode);
        Assert.True(ConfigFileHelper.TryGetPathValue(localConfig, "Daemon.Port", out var port));
        Assert.Equal(5299L, port);
        Assert.True(ConfigFileHelper.TryGetPathValue(localConfig, "Daemon.DisableSelfUpdate", out var disableSelfUpdate));
        Assert.Equal(true, disableSelfUpdate);
        Assert.False(ConfigFileHelper.TryGetPathValue(localConfig, "Daemon.Host", out _));
        Assert.False(ConfigFileHelper.TryGetPathValue(localConfig, "Daemon.TrustedProxies", out _));

        using (var vm = new ExposureModeConfigViewModel(Context.Paths))
        {
            Assert.Equal(ExposureMode.Local, vm.Step.SelectedMode);
            Assert.Equal("10.0.0.5", vm.Step.Host);
            Assert.Equal(["10.0.0.0/24"], vm.Step.TrustedProxies);

            vm.Step.SelectedMode = ExposureMode.ReverseProxy;
            vm.GoNext();
            vm.GoNext();
            vm.GoNext();
            vm.GoNext();
        }

        var reverseProxyConfig = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(reverseProxyConfig, "Daemon.ExposureMode", out var restoredMode));
        Assert.Equal("reverse-proxy", restoredMode);
        Assert.True(ConfigFileHelper.TryGetPathValue(reverseProxyConfig, "Daemon.Host", out var restoredHost));
        Assert.Equal("10.0.0.5", restoredHost);
        Assert.True(ConfigFileHelper.TryGetPathValue(reverseProxyConfig, "Daemon.TrustedProxies", out var restoredProxies));
        Assert.Equal(["10.0.0.0/24"], Assert.IsType<object[]>(restoredProxies).Select(static item => item.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public void Escape_from_saved_state_returns_to_mode_selection_before_parent_route()
    {
        using var vm = new ExposureModeConfigViewModel(Context.Paths);

        vm.GoNext();
        Assert.True(vm.IsSaved.Value);

        vm.GoBack();

        Assert.False(vm.IsSaved.Value);
        Assert.Equal(0, vm.Step.CurrentSubStep);
    }
}
