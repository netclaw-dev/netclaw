using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class ExposureModeDoctorCheckTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public ExposureModeDoctorCheckTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

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

        var check = BuildCheck(name => name == "tailscaled");

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("tailscale-serve", result.Message);
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

        var check = BuildCheck(name => name == "cloudflared");

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("cloudflare-tunnel", result.Message);
    }

    // ── Warning cases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Local_NonLoopbackHost_Warns()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "Host": "0.0.0.0", "ExposureMode": "local" }
            }
            """);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("0.0.0.0", result.Message);
        Assert.Contains("loopback", result.Message);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task Local_WithPublicIp_Warns()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Daemon": { "Host": "192.168.1.100", "ExposureMode": "local" }
            }
            """);

        var check = BuildCheck(_ => false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
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

    private void WriteConfig(string configText)
        => File.WriteAllText(_paths.NetclawConfigPath, configText);
}
