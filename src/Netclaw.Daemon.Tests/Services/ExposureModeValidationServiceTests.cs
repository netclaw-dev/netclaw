// -----------------------------------------------------------------------
// <copyright file="ExposureModeValidationServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ExposureModeValidationServiceTests
{
    // ── Local mode ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Local_LoopbackHost_SkipsAllValidation()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.Local };
        var sut = BuildService(config, _ => false); // no processes found

        // Must not throw
        await sut.StartAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.100")]
    public async Task Local_NonLoopbackHost_Throws(string host)
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.Local, Host = host };
        var sut = BuildService(config, _ => false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Invalid local topology", ex.Message);
        Assert.Contains(host, ex.Message);
    }

    // ── TailscaleServe ───────────────────────────────────────────────────────

    [Fact]
    public async Task TailscaleServe_WithTailscaledRunning_StartSucceeds()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleServe };
        var sut = BuildService(
            config,
            name => name == "tailscaled",
            remoteAuthSchemes: [new FakeRemoteAuthScheme("TestScheme")],
            deviceCount: 0);

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
        Assert.Contains("SkipTunnelProcessCheck", ex.Message);
    }

    // ── TailscaleFunnel ──────────────────────────────────────────────────────

    [Fact]
    public async Task TailscaleFunnel_WithTailscaledRunning_StartSucceeds()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleFunnel };
        var sut = BuildService(
            config,
            name => name == "tailscaled",
            remoteAuthSchemes: [new FakeRemoteAuthScheme("TestScheme")],
            deviceCount: 0);

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
        Assert.Contains("SkipTunnelProcessCheck", ex.Message);
    }

    // ── CloudflareTunnel ─────────────────────────────────────────────────────

    [Fact]
    public async Task CloudflareTunnel_WithCloudflaredRunning_StartSucceeds()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.CloudflareTunnel };
        var sut = BuildService(
            config,
            name => name == "cloudflared",
            remoteAuthSchemes: [new FakeRemoteAuthScheme("TestScheme")],
            deviceCount: 0);

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
        Assert.Contains("SkipTunnelProcessCheck", ex.Message);
    }

    [Theory]
    [InlineData(ExposureMode.TailscaleServe)]
    [InlineData(ExposureMode.TailscaleFunnel)]
    [InlineData(ExposureMode.CloudflareTunnel)]
    public async Task TunnelModes_WithSkipTunnelProcessCheck_AndRemoteAuth_StartSucceeds(ExposureMode mode)
    {
        var config = new DaemonConfig
        {
            ExposureMode = mode,
            SkipTunnelProcessCheck = true
        };
        var sut = BuildService(
            config,
            _ => false,
            remoteAuthSchemes: [new FakeRemoteAuthScheme("TestScheme")],
            deviceCount: 0);

        await sut.StartAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(ExposureMode.TailscaleServe)]
    [InlineData(ExposureMode.TailscaleFunnel)]
    [InlineData(ExposureMode.CloudflareTunnel)]
    public async Task TunnelModes_WithSkipTunnelProcessCheck_StillRequireRemoteAuth(ExposureMode mode)
    {
        var config = new DaemonConfig
        {
            ExposureMode = mode,
            SkipTunnelProcessCheck = true
        };
        // DeviceToken scheme IS wired (production reality), but no devices and no
        // alternative scheme → "no usable remote auth path", not a wiring error.
        var sut = BuildService(
            config,
            _ => false,
            remoteAuthSchemes: [new FakeRemoteAuthScheme(DeviceTokenAuthenticationHandler.SchemeName)],
            deviceCount: 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("No remote authentication available", ex.Message);
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

    // ── Remote auth guard: non-local + no devices + no scheme ────────────────

    [Fact]
    public async Task NonLocal_NoAltScheme_NoPairedDevices_Throws()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleServe };
        // DeviceToken wired (production reality), no devices, no alt scheme → must fail.
        var sut = BuildService(
            config,
            name => name == "tailscaled",
            remoteAuthSchemes: [new FakeRemoteAuthScheme(DeviceTokenAuthenticationHandler.SchemeName)],
            deviceCount: 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("No remote authentication available", ex.Message);
    }

    [Fact]
    public async Task NonLocal_NoAltScheme_NoPairedDevices_WithBootstrapSeeder_StartSucceeds()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleServe };
        var (bootstrapSeeder, deviceCounter) = BuildBootstrapSeeder();
        var sut = BuildService(
            config,
            name => name == "tailscaled",
            remoteAuthSchemes: [new FakeRemoteAuthScheme(DeviceTokenAuthenticationHandler.SchemeName)],
            deviceCounter: deviceCounter,
            bootstrapDeviceSeeder: bootstrapSeeder);

        await sut.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NonLocal_NoAltScheme_WithPairedDevices_StartSucceeds()
    {
        // DeviceToken scheme registered (production reality) + paired device → remote
        // clients CAN authenticate.
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleServe };
        var sut = BuildService(
            config,
            name => name == "tailscaled",
            remoteAuthSchemes: [new FakeRemoteAuthScheme(DeviceTokenAuthenticationHandler.SchemeName)],
            deviceCount: 1);

        await sut.StartAsync(TestContext.Current.CancellationToken);
    }

    // ── Wiring-integrity check (audit finding #25 companion) ─────────────────

    [Theory]
    [InlineData(ExposureMode.TailscaleServe)]
    [InlineData(ExposureMode.TailscaleFunnel)]
    [InlineData(ExposureMode.CloudflareTunnel)]
    public async Task TunnelModes_WithDeviceTokenSchemeUnregistered_ThrowsWiringError(ExposureMode mode)
    {
        // Simulates a broken DI wiring where the DeviceToken authentication scheme
        // is missing despite the daemon running in a tunnel exposure mode. Post-#24
        // (loopback bypass removed) this is the only legitimate remote-auth path for
        // tunnel modes, so a missing scheme must abort startup loudly rather than
        // serve an unauthenticatable surface.
        var config = new DaemonConfig
        {
            ExposureMode = mode,
            SkipTunnelProcessCheck = true,
        };
        var sut = BuildService(
            config,
            _ => false,
            remoteAuthSchemes: [],
            deviceCount: 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("device-bearer authentication scheme", ex.Message);
        Assert.Contains("DeviceToken", ex.Message);
    }

    [Fact]
    public async Task TunnelMode_WithAlternativeSchemeOnly_BypassesWiringCheck()
    {
        // An alternative remote-auth scheme is registered, so the DeviceToken scheme
        // is no longer the ONLY path; the wiring check tolerates a missing DeviceToken
        // in that case (the alternative scheme handles remote auth).
        var config = new DaemonConfig
        {
            ExposureMode = ExposureMode.TailscaleServe,
            SkipTunnelProcessCheck = true,
        };
        var sut = BuildService(
            config,
            _ => false,
            remoteAuthSchemes: [new FakeRemoteAuthScheme("OtherScheme")],
            deviceCount: 0);

        await sut.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReverseProxy_WithDeviceTokenSchemeUnregistered_DoesNotTriggerTunnelWiringCheck()
    {
        // ReverseProxy mode has its own auth topology (forwarded headers + TrustedProxies)
        // and is intentionally excluded from the tunnel wiring assertion. It should not
        // throw the device-bearer wiring error — it will throw the existing
        // "no remote authentication available" message instead.
        var config = new DaemonConfig
        {
            ExposureMode = ExposureMode.ReverseProxy,
            Host = "10.0.0.10",
            TrustedProxies = ["10.0.0.5"],
        };
        var sut = BuildService(
            config,
            _ => false,
            remoteAuthSchemes: [],
            deviceCount: 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain("device-bearer authentication scheme", ex.Message);
        Assert.Contains("No remote authentication available", ex.Message);
    }

    [Fact]
    public async Task NonLocal_WithRemoteAuthScheme_NoPairedDevices_StartSucceeds()
    {
        // An alternative remote auth scheme is registered → startup is allowed.
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleServe };
        var sut = BuildService(
            config,
            name => name == "tailscaled",
            remoteAuthSchemes: [new FakeRemoteAuthScheme("TestScheme")],
            deviceCount: 0);

        await sut.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NonLocal_WithOnlyDeviceBearerRegistration_NoPairedDevices_Throws()
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.TailscaleServe };
        var sut = BuildService(
            config,
            name => name == "tailscaled",
            remoteAuthSchemes: [new FakeRemoteAuthScheme(DeviceTokenAuthenticationHandler.SchemeName)],
            deviceCount: 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("No remote authentication available", ex.Message);
    }

    [Fact]
    public async Task ReverseProxy_WithLoopbackHost_Throws()
    {
        var config = new DaemonConfig
        {
            ExposureMode = ExposureMode.ReverseProxy,
            Host = "127.0.0.1",
            TrustedProxies = ["10.0.0.5"]
        };
        var sut = BuildService(
            config,
            _ => false,
            remoteAuthSchemes: [new FakeRemoteAuthScheme("TestScheme")],
            deviceCount: 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("loopback", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseProxy_WithInvalidTrustedProxy_Throws()
    {
        var config = new DaemonConfig
        {
            ExposureMode = ExposureMode.ReverseProxy,
            Host = "10.0.0.10",
            TrustedProxies = ["127.0.0.1/999"]
        };
        var sut = BuildService(
            config,
            _ => false,
            remoteAuthSchemes: [new FakeRemoteAuthScheme("TestScheme")],
            deviceCount: 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("127.0.0.1/999", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseProxy_WithNonLoopbackHostAndTrustedProxy_StartSucceeds()
    {
        var config = new DaemonConfig
        {
            ExposureMode = ExposureMode.ReverseProxy,
            Host = "10.0.0.10",
            TrustedProxies = ["10.0.0.5"]
        };
        var sut = BuildService(
            config,
            _ => false,
            remoteAuthSchemes: [new FakeRemoteAuthScheme("TestScheme")],
            deviceCount: 0);

        await sut.StartAsync(TestContext.Current.CancellationToken);
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
        Func<string, bool> processDetector,
        IEnumerable<IRemoteAuthSchemeRegistration>? remoteAuthSchemes = null,
        int deviceCount = 0,
        Func<CancellationToken, Task<int>>? deviceCounter = null,
        BootstrapDeviceSeeder? bootstrapDeviceSeeder = null)
    {
        return new ExposureModeValidationService(
            config,
            NullLogger<ExposureModeValidationService>.Instance,
            processDetector,
            remoteAuthSchemes,
            deviceCounter ?? (_ => Task.FromResult(deviceCount)),
            bootstrapDeviceSeeder);
    }

    private static (BootstrapDeviceSeeder Seeder, Func<CancellationToken, Task<int>> DeviceCounter) BuildBootstrapSeeder()
    {
        var basePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var registry = new DeviceRegistry(paths, timeProvider, NullLogger<DeviceRegistry>.Instance);
        return (
            new BootstrapDeviceSeeder(
                paths,
                registry,
                new BootstrapStateStore(paths),
                timeProvider,
                NullLogger<BootstrapDeviceSeeder>.Instance,
                new NullSecretsProtector()),
            async ct => (await registry.ListAsync(ct)).Count);
    }

    private sealed class FakeRemoteAuthScheme(string schemeName) : IRemoteAuthSchemeRegistration
    {
        public string SchemeName => schemeName;
    }
}
