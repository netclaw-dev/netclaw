// -----------------------------------------------------------------------
// <copyright file="ExposureModeDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class ExposureModeDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ExposureModeDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    // ── Pass cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Local_LoopbackHost_Passes()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "Host": "127.0.0.1", "ExposureMode": "local" }
            }
            """);

        var check = BuildCheck(_ => false); // no processes needed for local

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("local", result.Message);
        Assert.Contains("127.0.0.1", result.Message);
    }

    [Fact]
    public async Task MissingDaemonSection_DefaultsToLocalLoopback_Passes()
    {
        WriteConfig("""{ "configVersion": 1 }""");

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("local", result.Message);
    }

    [Fact]
    public async Task TailscaleServe_WithTailscaledRunning_Passes()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "tailscale-serve" }
            }
            """);
        WriteMatchingLocalDevice();

        var check = BuildCheck(name => name == "tailscaled");

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("tailscale-serve", result.Message);
    }

    [Fact]
    public async Task ReverseProxy_WithPairedDeviceAndTrustedProxy_Passes()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "10.0.0.10",
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["10.0.0.5"]
              }
            }
            """);

        WriteMatchingLocalDevice();

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("reverse-proxy", result.Message);
    }

    [Fact]
    public async Task TailscaleFunnel_WithTailscaledRunning_Passes()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "tailscale-funnel" }
            }
            """);
        WriteMatchingLocalDevice();

        var check = BuildCheck(name => name == "tailscaled");

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("tailscale-funnel", result.Message);
    }

    [Fact]
    public async Task CloudflareTunnel_WithCloudflaredRunning_Passes()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "cloudflare-tunnel" }
            }
            """);
        WriteMatchingLocalDevice();

        var check = BuildCheck(name => name == "cloudflared");

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("cloudflare-tunnel", result.Message);
    }

    // ── Local + non-loopback error cases ────────────────────────────────────

    [Fact]
    public async Task Local_NonLoopbackHost_IsError()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "Host": "0.0.0.0", "ExposureMode": "local" }
            }
            """);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("0.0.0.0", result.Message);
        Assert.Contains("loopback", result.Message);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task Local_WithPrivateIp_IsError()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "Host": "192.168.1.100", "ExposureMode": "local" }
            }
            """);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("192.168.1.100", result.Message);
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TailscaleServe_WithoutTailscaled_IsError()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "tailscale-serve" }
            }
            """);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("tailscale-serve", result.Message);
        Assert.Contains("tailscaled", result.Message);
        Assert.Contains("SkipTunnelProcessCheck", result.Message);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task TailscaleFunnel_WithoutTailscaled_IsError()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "tailscale-funnel" }
            }
            """);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("tailscale-funnel", result.Message);
        Assert.Contains("tailscaled", result.Message);
        Assert.Contains("SkipTunnelProcessCheck", result.Message);
    }

    [Fact]
    public async Task CloudflareTunnel_WithoutCloudflared_IsError()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "cloudflare-tunnel" }
            }
            """);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("cloudflare-tunnel", result.Message);
        Assert.Contains("cloudflared", result.Message);
        Assert.Contains("SkipTunnelProcessCheck", result.Message);
    }

    [Theory]
    [InlineData("tailscale-serve")]
    [InlineData("tailscale-funnel")]
    [InlineData("cloudflare-tunnel")]
    public async Task TunnelMode_SkipTunnelProcessCheck_WithPairedDevice_Passes(string mode)
    {
        WriteConfig($$"""
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "{{mode}}",
                "SkipTunnelProcessCheck": true
              }
            }
            """);
        WriteMatchingLocalDevice();

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains(mode, result.Message);
    }

    [Theory]
    [InlineData("tailscale-serve")]
    [InlineData("tailscale-funnel")]
    [InlineData("cloudflare-tunnel")]
    public async Task TunnelMode_SkipTunnelProcessCheck_StillRequiresRemoteAuth(string mode)
    {
        WriteConfig($$"""
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "{{mode}}",
                "SkipTunnelProcessCheck": true
              }
            }
            """);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("remote authentication", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseProxy_WithoutRemoteAuth_IsError()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "10.0.0.10",
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["10.0.0.5"]
              }
            }
            """);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("remote authentication", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseProxy_WithoutRemoteAuth_ButWithBootstrapTokenAndDevice_Passes()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "10.0.0.10",
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["10.0.0.5"]
              }
            }
            """);
        WriteMatchingLocalDevice("daemon-bootstrap");

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReverseProxy_WithMismatchedLocalBootstrapState_IsError()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "10.0.0.10",
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["10.0.0.5"]
              }
            }
            """);
        WritePairedDevice("daemon-bootstrap");
        File.WriteAllText(_paths.SecretsPath, "{\"configVersion\":1,\"DeviceToken\":\"bootstrap-token\"}");

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Bootstrap pairing state is incomplete", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseProxy_WithCompletedBootstrapAndMismatchedLocalToken_Warns()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "10.0.0.10",
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["10.0.0.5"]
              }
            }
            """);
        WritePairedDevice("daemon-bootstrap");
        File.WriteAllText(_paths.SecretsPath, "{\"configVersion\":1,\"DeviceToken\":\"stale-token\"}");
        new BootstrapStateStore(_paths).MarkCompleted(TimeProvider.System);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Local control-plane access is misconfigured", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseProxy_WithLoopbackHost_IsError()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "127.0.0.1",
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["10.0.0.5"]
              }
            }
            """);

        await File.WriteAllTextAsync(_paths.DevicesPath,
            """
            [{"Name":"laptop","TokenHash":"abc","Salt":"def","CreatedAt":"2026-01-01T00:00:00+00:00","LastUsedAt":"2026-01-01T00:00:00+00:00"}]
            """, TestContext.Current.CancellationToken);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("loopback", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseProxy_WithInvalidTrustedProxy_IsError()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "10.0.0.10",
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["not-an-ip"]
              }
            }
            """);

        await File.WriteAllTextAsync(_paths.DevicesPath,
            """
            [{"Name":"laptop","TokenHash":"abc","Salt":"def","CreatedAt":"2026-01-01T00:00:00+00:00","LastUsedAt":"2026-01-01T00:00:00+00:00"}]
            """, TestContext.Current.CancellationToken);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("not-an-ip", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseProxy_WithInvalidTrustedProxyCidr_IsError()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "10.0.0.10",
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["127.0.0.1/999"]
              }
            }
            """);

        WritePairedDevice();

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("127.0.0.1/999", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TailscaleServe_WrongProcessRunning_IsError()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "tailscale-serve" }
            }
            """);

        var check = BuildCheck(name => name == "cloudflared"); // wrong process

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("tailscaled", result.Message);
    }

    // ── Missing config file ───────────────────────────────────────────────────

    [Fact]
    public async Task MissingConfigFile_DelegatesToConfigReader()
    {
        // Don't write any config — DoctorJsonConfigReader returns Warning for missing file
        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Equal("Config File", result.Name);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ExposureModeDoctorCheck BuildCheck(Func<string, bool> processDetector)
        => new(_paths, processDetector);

    private void WritePairedDevice(string name = "laptop")
        => File.WriteAllText(_paths.DevicesPath,
            $$"""
            [
              {
                "Name": "{{name}}",
                "TokenHash": "abc",
                "Salt": "def",
                "CreatedAt": "2026-01-01T00:00:00+00:00",
                "LastUsedAt": "2026-01-01T00:00:00+00:00"
              }
            ]
            """);

    private void WriteMatchingLocalDevice(string name = "laptop")
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Base64Url.EncodeToString(tokenBytes);
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
        Span<byte> combined = stackalloc byte[tokenBytes.Length + saltBytes.Length];
        tokenBytes.CopyTo(combined);
        saltBytes.CopyTo(combined[tokenBytes.Length..]);
        var tokenHash = Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();

        File.WriteAllText(_paths.DevicesPath,
            $$"""
            [
              {
                "Name": "{{name}}",
                "TokenHash": "{{tokenHash}}",
                "Salt": "{{saltHex}}",
                "CreatedAt": "2026-01-01T00:00:00+00:00",
                "LastUsedAt": "2026-01-01T00:00:00+00:00"
              }
            ]
            """);

        File.WriteAllText(_paths.SecretsPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["DeviceToken"] = rawToken
        }));
    }

    private void WriteConfig(string configText)
        => File.WriteAllText(_paths.NetclawConfigPath, configText);
}
