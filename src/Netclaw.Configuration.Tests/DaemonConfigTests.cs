// -----------------------------------------------------------------------
// <copyright file="DaemonConfigTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Tests for <see cref="DaemonConfig"/> deserialization, defaults, and missing-section
/// behaviour, covering both the IConfiguration binding path and the STJ JSON path.
/// </summary>
public sealed class DaemonConfigTests
{
    // ── IConfiguration binding via BindFromConfiguration ─────────────────────

    [Fact]
    public void Defaults_applied_when_section_missing()
    {
        var config = new ConfigurationBuilder().Build();
        var result = DaemonConfig.BindFromConfiguration(config.GetSection("Daemon"));

        Assert.Equal("127.0.0.1", result.Host);
        Assert.Equal(5199, result.Port);
        Assert.Equal(ExposureMode.Local, result.ExposureMode);
        Assert.False(result.DisableSelfUpdate);
        Assert.False(result.SkipTunnelProcessCheck);
    }

    [Fact]
    public void Defaults_applied_when_null_section()
    {
        var result = DaemonConfig.BindFromConfiguration(null);

        Assert.Equal("127.0.0.1", result.Host);
        Assert.Equal(5199, result.Port);
        Assert.Equal(ExposureMode.Local, result.ExposureMode);
        Assert.False(result.DisableSelfUpdate);
        Assert.False(result.SkipTunnelProcessCheck);
    }

    [Theory]
    [InlineData("local", ExposureMode.Local)]
    [InlineData("reverse-proxy", ExposureMode.ReverseProxy)]
    [InlineData("tailscale-serve", ExposureMode.TailscaleServe)]
    [InlineData("tailscale-funnel", ExposureMode.TailscaleFunnel)]
    [InlineData("cloudflare-tunnel", ExposureMode.CloudflareTunnel)]
    public void BindFromConfiguration_parses_kebab_case_exposure_mode(
        string wireValue, ExposureMode expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Daemon:ExposureMode"] = wireValue
            })
            .Build();

        var result = DaemonConfig.BindFromConfiguration(config.GetSection("Daemon"));

        Assert.Equal(expected, result.ExposureMode);
    }

    [Fact]
    public void BindFromConfiguration_reads_host_and_port()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Daemon:Host"] = "0.0.0.0",
                ["Daemon:Port"] = "8443"
            })
            .Build();

        var result = DaemonConfig.BindFromConfiguration(config.GetSection("Daemon"));

        Assert.Equal("0.0.0.0", result.Host);
        Assert.Equal(8443, result.Port);
        Assert.Equal(ExposureMode.Local, result.ExposureMode);
    }

    [Fact]
    public void ParseExposureMode_throws_on_unknown_value()
    {
        Assert.Throws<InvalidOperationException>(
            () => DaemonConfig.ParseExposureMode("not-a-mode"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseExposureMode_returns_local_for_null_or_empty(string? value)
    {
        Assert.Equal(ExposureMode.Local, DaemonConfig.ParseExposureMode(value));
    }

    [Theory]
    [InlineData("stable", UpdateChannel.Stable)]
    [InlineData("beta", UpdateChannel.Beta)]
    [InlineData("BETA", UpdateChannel.Beta)]
    public void ParseUpdateChannel_parses_known_values(string value, UpdateChannel expected)
    {
        Assert.Equal(expected, DaemonConfig.ParseUpdateChannel(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseUpdateChannel_returns_stable_for_null_or_empty(string? value)
    {
        Assert.Equal(UpdateChannel.Stable, DaemonConfig.ParseUpdateChannel(value));
    }

    [Fact]
    public void ParseUpdateChannel_throws_on_unknown_value()
    {
        Assert.Throws<InvalidOperationException>(
            () => DaemonConfig.ParseUpdateChannel("nightly"));
    }

    [Fact]
    public void BindFromConfiguration_reads_DisableSelfUpdate_true()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Daemon:DisableSelfUpdate"] = "true"
            })
            .Build();

        var result = DaemonConfig.BindFromConfiguration(config.GetSection("Daemon"));

        Assert.True(result.DisableSelfUpdate);
    }

    [Fact]
    public void BindFromConfiguration_reads_SkipTunnelProcessCheck_true()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Daemon:SkipTunnelProcessCheck"] = "true"
            })
            .Build();

        var result = DaemonConfig.BindFromConfiguration(config.GetSection("Daemon"));

        Assert.True(result.SkipTunnelProcessCheck);
    }

    // ── System.Text.Json serialization path ──────────────────────────────────

    [Theory]
    [InlineData("local", ExposureMode.Local)]
    [InlineData("reverse-proxy", ExposureMode.ReverseProxy)]
    [InlineData("tailscale-serve", ExposureMode.TailscaleServe)]
    [InlineData("tailscale-funnel", ExposureMode.TailscaleFunnel)]
    [InlineData("cloudflare-tunnel", ExposureMode.CloudflareTunnel)]
    public void Json_deserializes_kebab_case_exposure_mode(
        string wireValue, ExposureMode expected)
    {
        var json = $$"""{"ExposureMode": "{{wireValue}}"}""";
        var result = JsonSerializer.Deserialize<DaemonConfig>(json);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.ExposureMode);
    }

    [Theory]
    [InlineData(ExposureMode.Local, "local")]
    [InlineData(ExposureMode.ReverseProxy, "reverse-proxy")]
    [InlineData(ExposureMode.TailscaleServe, "tailscale-serve")]
    [InlineData(ExposureMode.TailscaleFunnel, "tailscale-funnel")]
    [InlineData(ExposureMode.CloudflareTunnel, "cloudflare-tunnel")]
    public void Json_serializes_exposure_mode_to_kebab_case(
        ExposureMode mode, string expectedWire)
    {
        var config = new DaemonConfig { ExposureMode = mode };
        var json = JsonSerializer.Serialize(config);

        Assert.Contains($"\"ExposureMode\":\"{expectedWire}\"", json);
    }

    [Fact]
    public void BindFromConfiguration_reads_trusted_proxies()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Daemon:TrustedProxies:0"] = "10.0.0.5",
                ["Daemon:TrustedProxies:1"] = "10.0.0.0/24"
            })
            .Build();

        var result = DaemonConfig.BindFromConfiguration(config.GetSection("Daemon"));

        Assert.Equal(["10.0.0.5", "10.0.0.0/24"], result.TrustedProxies);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.100")]
    public void Validator_rejects_non_loopback_host_in_local_mode(string host)
    {
        var issues = DaemonExposureValidator.Validate(
            new DaemonConfig
            {
                ExposureMode = ExposureMode.Local,
                Host = host
            },
            hasRemoteAuthenticationPath: false);

        Assert.Contains(issues, issue => issue.Message.Contains("Invalid local topology", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void Validator_accepts_loopback_host_in_local_mode(string host)
    {
        var issues = DaemonExposureValidator.Validate(
            new DaemonConfig
            {
                ExposureMode = ExposureMode.Local,
                Host = host
            },
            hasRemoteAuthenticationPath: false);

        Assert.Empty(issues);
    }

    [Fact]
    public void Validator_rejects_loopback_reverse_proxy_topology()
    {
        var issues = DaemonExposureValidator.Validate(
            new DaemonConfig
            {
                ExposureMode = ExposureMode.ReverseProxy,
                Host = "127.0.0.1",
                TrustedProxies = ["10.0.0.5"]
            },
            hasRemoteAuthenticationPath: true);

        Assert.Contains(issues, issue => issue.Message.Contains("loopback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_rejects_invalid_trusted_proxy_entry()
    {
        var issues = DaemonExposureValidator.Validate(
            new DaemonConfig
            {
                ExposureMode = ExposureMode.ReverseProxy,
                Host = "10.0.0.10",
                TrustedProxies = ["not-an-ip"]
            },
            hasRemoteAuthenticationPath: true);

        Assert.Contains(issues, issue => issue.Message.Contains("not-an-ip", StringComparison.OrdinalIgnoreCase));
    }
}
