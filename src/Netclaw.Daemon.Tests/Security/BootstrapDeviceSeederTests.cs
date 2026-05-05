// -----------------------------------------------------------------------
// <copyright file="BootstrapDeviceSeederTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

public sealed class BootstrapDeviceSeederTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly NetclawPaths _paths;
    private readonly DeviceRegistry _deviceRegistry;
    private readonly BootstrapStateStore _bootstrapStateStore;
    private readonly BootstrapDeviceSeeder _sut;

    public BootstrapDeviceSeederTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _deviceRegistry = new DeviceRegistry(_paths, _time, NullLogger<DeviceRegistry>.Instance);
        _bootstrapStateStore = new BootstrapStateStore(_paths);
        _sut = new BootstrapDeviceSeeder(
            _paths,
            _deviceRegistry,
            _bootstrapStateStore,
            _time,
            NullLogger<BootstrapDeviceSeeder>.Instance,
            SecretsProtection.CreateProtector(_paths));
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task EnsureSeededAsync_seeds_device_and_local_token_for_non_local_mode()
    {
        var seeded = await _sut.EnsureSeededAsync(
            new DaemonConfig { ExposureMode = ExposureMode.ReverseProxy },
            TestContext.Current.CancellationToken);

        Assert.True(seeded);
        var devices = await _deviceRegistry.ListAsync(TestContext.Current.CancellationToken);
        var device = Assert.Single(devices);
        Assert.Contains("bootstrap", device.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(device.IsBootstrapDevice);
        Assert.True(File.Exists(_paths.SecretsPath));
        var secretsText = File.ReadAllText(_paths.SecretsPath);
        Assert.Contains("DeviceToken", secretsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureSeededAsync_skips_when_device_already_exists()
    {
        await _deviceRegistry.AddAsync(
            DeviceTestHelpers.MakeDevice("existing", _time.GetUtcNow()).Device,
            TestContext.Current.CancellationToken);

        var seeded = await _sut.EnsureSeededAsync(
            new DaemonConfig { ExposureMode = ExposureMode.TailscaleServe },
            TestContext.Current.CancellationToken);

        Assert.False(seeded);
        var devices = await _deviceRegistry.ListAsync(TestContext.Current.CancellationToken);
        Assert.Single(devices);
    }

    [Fact]
    public async Task EnsureSeededAsync_skips_after_completion_marker_written()
    {
        _sut.MarkCompleted();

        var seeded = await _sut.EnsureSeededAsync(
            new DaemonConfig { ExposureMode = ExposureMode.ReverseProxy },
            TestContext.Current.CancellationToken);

        Assert.False(seeded);
        var devices = await _deviceRegistry.ListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(devices);
    }

    [Fact]
    public async Task EnsureSeededAsync_skips_for_local_mode()
    {
        var seeded = await _sut.EnsureSeededAsync(
            new DaemonConfig { ExposureMode = ExposureMode.Local },
            TestContext.Current.CancellationToken);

        Assert.False(seeded);
        Assert.Empty(await _deviceRegistry.ListAsync(TestContext.Current.CancellationToken));
    }
}
