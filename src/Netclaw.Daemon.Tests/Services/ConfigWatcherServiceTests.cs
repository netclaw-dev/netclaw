// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigWatcherServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeRestartCoordinator _restartCoordinator;
    private readonly FakeTimeProvider _time = new();
    private readonly ConfigWatcherService _sut;

    public ConfigWatcherServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        _restartCoordinator = new FakeRestartCoordinator();

        _sut = new ConfigWatcherService(
            _paths,
            _time,
            _restartCoordinator,
            NullLogger<ConfigWatcherService>.Instance);
    }

    [Fact]
    public async Task ValidConfigChange_TriggersRestart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Theory]
    [InlineData("secrets.json")]
    [InlineData("mcp-oauth-metadata.json")]
    [InlineData("random.txt")]
    [InlineData(null)]
    public void NonConfigFiles_AreNotWatched(string? fileName)
    {
        Assert.False(ConfigWatcherService.IsWatchedFile(fileName));
    }

    [Fact]
    public void NetclawJson_IsWatched()
    {
        Assert.True(ConfigWatcherService.IsWatchedFile("netclaw.json"));
    }

    [Fact]
    public async Task InvalidJson_DoesNotTriggerRestart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ broken json """);
        // secrets.json doesn't exist — that's fine (optional)

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task MissingConfigFiles_TriggersRestart()
    {
        // Both files are optional in the config chain — missing = valid
        Assert.False(File.Exists(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task RestartCoordinatorFailure_DoesNotLeaveIngressClosed()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        _restartCoordinator.ThrowOnRequest = true;

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task DaemonSectionChange_TriggersRestart()
    {
        // Regression guard for #1279: Daemon-section changes (bind address, exposure
        // mode) are now applied via the coordinated in-process restart rather than
        // skipped. The restart rebuilds the host and re-binds, and the init wizard
        // relies on this so that writing a new exposure mode actually takes effect.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Daemon": { "Port": 9999, "ExposureMode": "reverse-proxy" } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    // File-event tests — drive the watcher's event handlers directly with
    // synthesized event args. Real FileSystemWatcher delivery is OS-dependent
    // and cannot be observed deterministically (especially the latency and
    // event classification of an atomic-replace move on Windows), so the
    // handler -> debounce -> reload pipeline is exercised in isolation and the
    // debounce is virtualized through the injected FakeTimeProvider.

    [Fact]
    public async Task AtomicReplace_TriggersReload()
    {
        // An atomic-replace write (write-temp then rename) surfaces as a Renamed
        // event whose new name is the watched config file.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath)!;

        _sut.OnFileRenamed(this, new RenamedEventArgs(
            WatcherChangeTypes.Renamed, configDir, "netclaw.json", "netclaw.json.tmp.0a1b2c3d"));

        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload;

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task InPlaceWrite_TriggersReload()
    {
        // Regression: a direct in-place write (e.g. a shell > redirect) surfaces
        // as a Changed event and must still trigger a reload.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath)!;

        _sut.OnFileChanged(this, new FileSystemEventArgs(
            WatcherChangeTypes.Changed, configDir, "netclaw.json"));

        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload;

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dir.Dispose();
    }

    private sealed class FakeRestartCoordinator : IDaemonRestartCoordinator
    {
        public int RequestCount { get; private set; }

        public bool ThrowOnRequest { get; set; }

        public Task RequestConfigRestartAsync(CancellationToken cancellationToken)
        {
            RequestCount++;

            if (ThrowOnRequest)
                throw new InvalidOperationException("boom");

            return Task.CompletedTask;
        }
    }
}
