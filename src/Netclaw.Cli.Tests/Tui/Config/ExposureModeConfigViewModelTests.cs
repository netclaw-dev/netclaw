// -----------------------------------------------------------------------
// <copyright file="ExposureModeConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Runtime.InteropServices;
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
    public void Constructor_with_malformed_config_does_not_throw_and_surfaces_error()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath, "{ not valid json ");

        // Must not throw from the constructor (which would make the Exposure page inaccessible); it
        // degrades to no existing config and surfaces the read error.
        using var vm = new ExposureModeConfigViewModel(Context.Paths);

        Assert.Contains("Could not read", vm.Context.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
    }

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
    public void Saving_non_local_with_orphaned_local_token_pairs_current_client()
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
        // A real DeviceToken is always a base64url token; orphaned = present in secrets with no
        // matching device in the (absent) registry.
        var (orphanedToken, _) = CreatePairedDevice("orphan");
        File.WriteAllText(Context.Paths.SecretsPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["DeviceToken"] = orphanedToken
        }));

        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.TailscaleServe;

        AdvanceTunnelModeToSave(vm);

        // Auto-pair instead of blocking: keep the operator's existing token and mint a device that
        // accepts it so the configuring client is not locked out of chat.
        Assert.True(vm.IsSaved.Value);
        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.ExposureMode", out var mode));
        Assert.Equal("tailscale-serve", mode);
        Assert.Equal(orphanedToken, ReadLocalDeviceToken());
        var device = Assert.Single(ReadPairedDevices());
        Assert.True(device.IsBootstrapDevice);
        Assert.True(PairedDevice.VerifyToken(orphanedToken, device));
    }

    [Fact]
    public void Pairing_writes_devices_registry_atomically_with_owner_only_permissions()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "local" }
            }
            """);
        File.WriteAllText(Context.Paths.DevicesPath, "[]");

        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.TailscaleServe;

        AdvanceTunnelModeToSave(vm);

        Assert.True(vm.IsSaved.Value);
        Assert.Single(ReadPairedDevices());
        // The atomic write leaves no temp sibling behind, and devices.json stays owner-only.
        var dir = Path.GetDirectoryName(Context.Paths.DevicesPath)!;
        Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Context.Paths.DevicesPath));
    }

    [Fact]
    public void Saving_non_local_with_empty_devices_file_pairs_current_client()
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

        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.TailscaleServe;

        AdvanceTunnelModeToSave(vm);

        // No token and an empty registry: mint a fresh token+device for the configuring client.
        Assert.True(vm.IsSaved.Value);
        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.ExposureMode", out var mode));
        Assert.Equal("tailscale-serve", mode);
        var rawToken = ReadLocalDeviceToken();
        Assert.False(string.IsNullOrWhiteSpace(rawToken));
        var device = Assert.Single(ReadPairedDevices());
        Assert.True(device.IsBootstrapDevice);
        Assert.True(PairedDevice.VerifyToken(rawToken, device));
    }

    [Fact]
    public void Saving_non_local_with_mismatched_local_token_pairs_current_client()
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

        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.TailscaleServe;

        AdvanceTunnelModeToSave(vm);

        // The local token matches no registered device: mint an additional device that accepts it
        // without removing the pre-existing one, so the configuring client retains access.
        Assert.True(vm.IsSaved.Value);
        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.ExposureMode", out var mode));
        Assert.Equal("tailscale-serve", mode);
        Assert.Equal(mismatchedToken, ReadLocalDeviceToken());
        var devices = ReadPairedDevices();
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => PairedDevice.VerifyToken(mismatchedToken, d));
        Assert.Contains(devices, d => d.Name == registeredDevice.Name);
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
    public void Saving_when_registry_write_fails_surfaces_error_without_crashing_or_claiming_success()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "local" }
            }
            """);
        // Force the auto-pair devices-registry access to throw the way a disk-full / permission
        // failure would: ReadPairedDevices/WritePairedDevices cannot read or atomically replace a
        // path that is a directory, raising IOException/UnauthorizedAccessException at the real
        // call site. Before the guard, that exception escaped GoNext into the Termina event loop.
        Directory.CreateDirectory(Context.Paths.DevicesPath);

        using var vm = new ExposureModeConfigViewModel(Context.Paths);
        vm.Step.SelectedMode = ExposureMode.TailscaleServe;

        // Must not throw: the write failure has to be caught and surfaced, not crash the loop.
        AdvanceTunnelModeToSave(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.Contains("Failed to save exposure mode", vm.Context.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
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
