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

    // ── WriteConfig → optional setup → BuildCheck → RunAsync → severity/message ──

    public static TheoryData<ExposureCase> Cases()
    {
        var reverseProxyHeader = """
            {
              "configVersion": 1,
              "Daemon": {
                "Host": "10.0.0.10",
                "ExposureMode": "reverse-proxy",
                "TrustedProxies": ["10.0.0.5"]
              }
            }
            """;

        return new TheoryData<ExposureCase>
        {
            // ── Pass cases ───────────────────────────────────────────────
            new ExposureCase("Local_LoopbackHost_Passes",
                """{ "configVersion": 1, "Daemon": { "Host": "127.0.0.1", "ExposureMode": "local" } }""",
                null, _ => false, DoctorSeverity.Pass, ["local", "127.0.0.1"], false, false),
            new ExposureCase("MissingDaemonSection_DefaultsToLocalLoopback_Passes",
                """{ "configVersion": 1 }""",
                null, _ => false, DoctorSeverity.Pass, ["local"], false, false),
            new ExposureCase("TailscaleServe_WithTailscaledRunning_Passes",
                """{ "configVersion": 1, "Daemon": { "ExposureMode": "tailscale-serve" } }""",
                t => t.WriteMatchingLocalDevice(), name => name == "tailscaled",
                DoctorSeverity.Pass, ["tailscale-serve"], false, false),
            new ExposureCase("ReverseProxy_WithPairedDeviceAndTrustedProxy_Passes",
                reverseProxyHeader,
                t => t.WriteMatchingLocalDevice(), _ => false, DoctorSeverity.Pass, ["reverse-proxy"], false, false),
            new ExposureCase("TailscaleFunnel_WithTailscaledRunning_Passes",
                """{ "configVersion": 1, "Daemon": { "ExposureMode": "tailscale-funnel" } }""",
                t => t.WriteMatchingLocalDevice(), name => name == "tailscaled",
                DoctorSeverity.Pass, ["tailscale-funnel"], false, false),
            new ExposureCase("CloudflareTunnel_WithCloudflaredRunning_Passes",
                """{ "configVersion": 1, "Daemon": { "ExposureMode": "cloudflare-tunnel" } }""",
                t => t.WriteMatchingLocalDevice(), name => name == "cloudflared",
                DoctorSeverity.Pass, ["cloudflare-tunnel"], false, false),

            // ── Local + non-loopback error cases ────────────────────────
            new ExposureCase("Local_NonLoopbackHost_IsError",
                """{ "configVersion": 1, "Daemon": { "Host": "0.0.0.0", "ExposureMode": "local" } }""",
                null, _ => false, DoctorSeverity.Error, ["0.0.0.0", "loopback"], false, true),
            new ExposureCase("Local_WithPrivateIp_IsError",
                """{ "configVersion": 1, "Daemon": { "Host": "192.168.1.100", "ExposureMode": "local" } }""",
                null, _ => false, DoctorSeverity.Error, ["192.168.1.100"], false, false),

            // ── Error cases ──────────────────────────────────────────────
            new ExposureCase("TailscaleServe_WithoutTailscaled_IsError",
                """{ "configVersion": 1, "Daemon": { "ExposureMode": "tailscale-serve" } }""",
                null, _ => false, DoctorSeverity.Error,
                ["tailscale-serve", "tailscaled", "SkipTunnelProcessCheck"], false, true),
            new ExposureCase("TailscaleFunnel_WithoutTailscaled_IsError",
                """{ "configVersion": 1, "Daemon": { "ExposureMode": "tailscale-funnel" } }""",
                null, _ => false, DoctorSeverity.Error,
                ["tailscale-funnel", "tailscaled", "SkipTunnelProcessCheck"], false, false),
            new ExposureCase("CloudflareTunnel_WithoutCloudflared_IsError",
                """{ "configVersion": 1, "Daemon": { "ExposureMode": "cloudflare-tunnel" } }""",
                null, _ => false, DoctorSeverity.Error,
                ["cloudflare-tunnel", "cloudflared", "SkipTunnelProcessCheck"], false, false),
            new ExposureCase("ReverseProxy_WithoutRemoteAuth_IsError",
                reverseProxyHeader,
                null, _ => false, DoctorSeverity.Error, ["remote authentication"], true, false),
            new ExposureCase("ReverseProxy_WithoutRemoteAuth_ButWithBootstrapTokenAndDevice_Passes",
                reverseProxyHeader,
                t => t.WriteMatchingLocalDevice("daemon-bootstrap"), _ => false,
                DoctorSeverity.Pass, [], false, false),
            new ExposureCase("ReverseProxy_WithMismatchedLocalBootstrapState_IsError",
                reverseProxyHeader,
                t =>
                {
                    t.WritePairedDevice("daemon-bootstrap");
                    File.WriteAllText(t._paths.SecretsPath, "{\"configVersion\":1,\"DeviceToken\":\"bootstrap-token\"}");
                },
                _ => false, DoctorSeverity.Error, ["Bootstrap pairing state is incomplete"], true, false),
            new ExposureCase("ReverseProxy_WithCompletedBootstrapAndMismatchedLocalToken_Warns",
                reverseProxyHeader,
                t =>
                {
                    t.WritePairedDevice("daemon-bootstrap");
                    File.WriteAllText(t._paths.SecretsPath, "{\"configVersion\":1,\"DeviceToken\":\"stale-token\"}");
                    new BootstrapStateStore(t._paths).MarkCompleted(TimeProvider.System);
                },
                _ => false, DoctorSeverity.Warning, ["Local control-plane access is misconfigured"], true, false),
            new ExposureCase("ReverseProxy_WithLoopbackHost_IsError",
                """
                {
                  "configVersion": 1,
                  "Daemon": {
                    "Host": "127.0.0.1",
                    "ExposureMode": "reverse-proxy",
                    "TrustedProxies": ["10.0.0.5"]
                  }
                }
                """,
                t => t.WritePairedDevice(), _ => false, DoctorSeverity.Error, ["loopback"], true, false),
            new ExposureCase("ReverseProxy_WithInvalidTrustedProxy_IsError",
                """
                {
                  "configVersion": 1,
                  "Daemon": {
                    "Host": "10.0.0.10",
                    "ExposureMode": "reverse-proxy",
                    "TrustedProxies": ["not-an-ip"]
                  }
                }
                """,
                t => t.WritePairedDevice(), _ => false, DoctorSeverity.Error, ["not-an-ip"], true, false),
            new ExposureCase("ReverseProxy_WithInvalidTrustedProxyCidr_IsError",
                """
                {
                  "configVersion": 1,
                  "Daemon": {
                    "Host": "10.0.0.10",
                    "ExposureMode": "reverse-proxy",
                    "TrustedProxies": ["127.0.0.1/999"]
                  }
                }
                """,
                t => t.WritePairedDevice(), _ => false, DoctorSeverity.Error, ["127.0.0.1/999"], true, false),
            new ExposureCase("TailscaleServe_WrongProcessRunning_IsError",
                """{ "configVersion": 1, "Daemon": { "ExposureMode": "tailscale-serve" } }""",
                null, name => name == "cloudflared", // wrong process
                DoctorSeverity.Error, ["tailscaled"], false, false),
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExposureMode_MatchesExpectedSeverityAndMessage(ExposureCase testCase)
    {
        WriteConfig(testCase.ConfigJson);
        testCase.ExtraSetup?.Invoke(this);

        var check = BuildCheck(testCase.ProcessPredicate);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(testCase.ExpectedSeverity, result.Severity);
        foreach (var expected in testCase.ExpectedContains)
        {
            if (testCase.IgnoreCase)
            {
                Assert.Contains(expected, result.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Contains(expected, result.Message);
            }
        }

        if (testCase.ExpectRemediation)
        {
            Assert.NotNull(result.Remediation);
        }
    }

    public sealed record ExposureCase(
        string Name,
        string ConfigJson,
        Action<ExposureModeDoctorCheckTests>? ExtraSetup,
        Func<string, bool> ProcessPredicate,
        DoctorSeverity ExpectedSeverity,
        string[] ExpectedContains,
        bool IgnoreCase,
        bool ExpectRemediation)
    {
        public override string ToString() => Name;
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
