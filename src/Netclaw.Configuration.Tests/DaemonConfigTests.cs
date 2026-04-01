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
    }

    [Fact]
    public void Defaults_applied_when_null_section()
    {
        var result = DaemonConfig.BindFromConfiguration(null);

        Assert.Equal("127.0.0.1", result.Host);
        Assert.Equal(5199, result.Port);
        Assert.Equal(ExposureMode.Local, result.ExposureMode);
    }

    [Theory]
    [InlineData("local", ExposureMode.Local)]
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

    [Fact]
    public void ParseExposureMode_returns_local_for_null()
    {
        Assert.Equal(ExposureMode.Local, DaemonConfig.ParseExposureMode(null));
    }

    // ── System.Text.Json serialization path ──────────────────────────────────

    [Theory]
    [InlineData("local", ExposureMode.Local)]
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
}
