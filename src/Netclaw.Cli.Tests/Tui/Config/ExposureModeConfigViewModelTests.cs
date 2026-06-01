// -----------------------------------------------------------------------
// <copyright file="ExposureModeConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
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
    public void Saving_first_non_local_mode_auto_pairs_current_client()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "local"
              }
            }
            """);

        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.TailscaleServe;

        AdvanceTunnelModeToSave(vm);

        Assert.True(vm.IsSaved.Value);
        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.ExposureMode", out var mode));
        Assert.Equal("tailscale-serve", mode);

        var rawToken = ReadLocalDeviceToken();
        var devices = ReadPairedDevices();
        var device = Assert.Single(devices);
        Assert.True(device.IsBootstrapDevice);
        Assert.True(PairedDevice.VerifyToken(rawToken, device));
    }

    [Fact]
    public void Saving_non_local_with_orphaned_local_token_blocks_before_persistence()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "local"
              }
            }
            """);
        File.WriteAllText(Context.Paths.SecretsPath, "{\"configVersion\":1,\"DeviceToken\":\"orphaned-token\"}");
        var configBefore = File.ReadAllText(Context.Paths.NetclawConfigPath);

        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.TailscaleServe;

        AdvanceTunnelModeToSave(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.Contains("netclaw doctor", vm.Context.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docs/spec/SPEC-006-gateway-exposure-and-remote-access.md", vm.Context.StatusMessage.Value, StringComparison.Ordinal);
        Assert.Contains("#875", vm.Context.StatusMessage.Value, StringComparison.Ordinal);
        Assert.Equal(configBefore, File.ReadAllText(Context.Paths.NetclawConfigPath));
        Assert.False(File.Exists(Context.Paths.DevicesPath));
    }

    [Fact]
    public void Saving_non_local_with_empty_devices_file_blocks_before_persistence()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "local"
              }
            }
            """);
        File.WriteAllText(Context.Paths.DevicesPath, "[]");
        var configBefore = File.ReadAllText(Context.Paths.NetclawConfigPath);

        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.TailscaleServe;

        AdvanceTunnelModeToSave(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.Contains("netclaw doctor", vm.Context.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#875", vm.Context.StatusMessage.Value, StringComparison.Ordinal);
        Assert.Equal(configBefore, File.ReadAllText(Context.Paths.NetclawConfigPath));
        Assert.Equal("[]", File.ReadAllText(Context.Paths.DevicesPath));
    }

    [Fact]
    public void Saving_non_local_with_mismatched_local_token_blocks_before_persistence()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "local"
              }
            }
            """);
        var (_, registeredDevice) = CreatePairedDevice("daemon-bootstrap");
        var (mismatchedToken, _) = CreatePairedDevice("other-device");
        WritePairedDevice(registeredDevice);
        File.WriteAllText(Context.Paths.SecretsPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["DeviceToken"] = mismatchedToken
        }));
        var configBefore = File.ReadAllText(Context.Paths.NetclawConfigPath);

        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.TailscaleServe;

        AdvanceTunnelModeToSave(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.Contains("Bootstrap pairing state", vm.Context.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#875", vm.Context.StatusMessage.Value, StringComparison.Ordinal);
        Assert.Equal(configBefore, File.ReadAllText(Context.Paths.NetclawConfigPath));
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
    public void Saving_reverse_proxy_with_loopback_host_blocks_before_persistence()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
        var configBefore = File.ReadAllText(Context.Paths.NetclawConfigPath);
        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.ReverseProxy;
        vm.Step.Host = "127.0.0.1";
        vm.Step.TrustedProxies = ["10.0.0.0/24"];

        AdvanceReverseProxyToSave(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.Contains("loopback", vm.Context.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(configBefore, File.ReadAllText(Context.Paths.NetclawConfigPath));
    }

    [Fact]
    public void Saving_reverse_proxy_with_invalid_trusted_proxy_blocks_before_persistence()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
        var configBefore = File.ReadAllText(Context.Paths.NetclawConfigPath);
        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.ReverseProxy;
        vm.Step.Host = "10.0.0.5";
        vm.Step.TrustedProxies = ["not-a-proxy"];

        AdvanceReverseProxyToSave(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.Contains("not-a-proxy", vm.Context.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(configBefore, File.ReadAllText(Context.Paths.NetclawConfigPath));
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

    private static void AdvanceReverseProxyToSave(ExposureModeConfigViewModel vm)
    {
        vm.GoNext();
        vm.GoNext();
        vm.GoNext();
        vm.GoNext();
    }

    private static void AdvanceTunnelModeToSave(ExposureModeConfigViewModel vm)
    {
        vm.GoNext();
        vm.GoNext();
    }

    private string ReadLocalDeviceToken()
    {
        var secrets = ConfigFileHelper.LoadJsonDict(Context.Paths.SecretsPath);
        Assert.True(secrets.TryGetValue("DeviceToken", out var rawValue));
        var rawToken = rawValue is JsonElement element ? element.GetString() : rawValue?.ToString();
        return ConfigFileHelper.DecryptIfEncrypted(Context.Paths, rawToken);
    }

    private List<PairedDevice> ReadPairedDevices()
        => JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(Context.Paths.DevicesPath)) ?? [];

    private void WritePairedDevice(PairedDevice device)
        => File.WriteAllText(Context.Paths.DevicesPath, JsonSerializer.Serialize(new[] { device }));

    private static (string RawToken, PairedDevice Device) CreatePairedDevice(string name)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Base64Url.EncodeToString(tokenBytes);
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
        var tokenHash = PairedDevice.ComputeTokenHash(rawToken, saltHex);

        return (rawToken, new PairedDevice
        {
            Name = name,
            IsBootstrapDevice = true,
            TokenHash = tokenHash,
            Salt = saltHex,
            CreatedAt = DateTimeOffset.UnixEpoch,
            LastUsedAt = DateTimeOffset.UnixEpoch,
        });
    }
}
