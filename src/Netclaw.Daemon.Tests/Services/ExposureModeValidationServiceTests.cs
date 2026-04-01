using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ExposureModeValidationServiceTests
{
    // ── Local mode ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Local_SkipsAllValidation_EvenWhenNoProcessesExist()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.Local };
        var sut = BuildService(config, _ => false); // no processes found

        // Must not throw
        await sut.StartAsync(TestContext.Current.CancellationToken);
    }

    // ── TailscaleServe ───────────────────────────────────────────────────────

    [Fact]
    public async Task TailscaleServe_WithTailscaledRunning_StartSucceeds()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleServe };
        var sut = BuildService(config, name => name == "tailscaled");

        await sut.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TailscaleServe_WithoutTailscaled_Throws()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleServe };
        var sut = BuildService(config, _ => false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("tailscale-serve", ex.Message);
        Assert.Contains("tailscaled", ex.Message);
    }

    // ── TailscaleFunnel ──────────────────────────────────────────────────────

    [Fact]
    public async Task TailscaleFunnel_WithTailscaledRunning_StartSucceeds()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleFunnel };
        var sut = BuildService(config, name => name == "tailscaled");

        await sut.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TailscaleFunnel_WithoutTailscaled_Throws()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleFunnel };
        var sut = BuildService(config, _ => false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("tailscale-funnel", ex.Message);
        Assert.Contains("tailscaled", ex.Message);
    }

    // ── CloudflareTunnel ─────────────────────────────────────────────────────

    [Fact]
    public async Task CloudflareTunnel_WithCloudflaredRunning_StartSucceeds()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.CloudflareTunnel };
        var sut = BuildService(config, name => name == "cloudflared");

        await sut.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CloudflareTunnel_WithoutCloudflared_Throws()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.CloudflareTunnel };
        var sut = BuildService(config, _ => false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("cloudflare-tunnel", ex.Message);
        Assert.Contains("cloudflared", ex.Message);
    }

    // ── Cross-mode: wrong process running ────────────────────────────────────

    [Fact]
    public async Task TailscaleServe_WithOnlyCloudflaredRunning_Throws()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleServe };
        var sut = BuildService(config, name => name == "cloudflared"); // wrong process

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));
    }

    // ── StopAsync is always a no-op ──────────────────────────────────────────

    [Fact]
    public async Task StopAsync_AlwaysCompletesSuccessfully()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.Local };
        var sut = BuildService(config, _ => false);

        await sut.StopAsync(TestContext.Current.CancellationToken);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ExposureModeValidationService BuildService(
        DaemonConfig config,
        Func<string, bool> processDetector)
    {
        return new ExposureModeValidationService(
            config,
            NullLogger<ExposureModeValidationService>.Instance,
            processDetector);
    }
}
