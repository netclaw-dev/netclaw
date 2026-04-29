// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigWatcherServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly FakeRestartCoordinator _restartCoordinator;
    private readonly ConfigWatcherService _sut;

    public ConfigWatcherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();

        _restartCoordinator = new FakeRestartCoordinator();

        _sut = new ConfigWatcherService(
            _paths,
            TimeProvider.System,
            _restartCoordinator,
            new DaemonConfig(),
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
    public async Task DaemonSectionChanged_SkipsRestartAndLogsWarning()
    {
        // Port differs from the default DaemonConfig (5199) injected into _sut
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Daemon": { "Port": 9999 } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task DaemonSectionMatchingCurrentConfig_TriggersRestart()
    {
        // Explicit Daemon section that matches the running defaults — not a change
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Daemon": { "Host": "127.0.0.1", "Port": 5199, "ExposureMode": "local" } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public void ReadDaemonConfigFromFile_MissingFile_ReturnsDefaults()
    {
        var result = ConfigWatcherService.ReadDaemonConfigFromFile(_paths.NetclawConfigPath);

        Assert.Equal(new DaemonConfig(), result);
    }

    [Fact]
    public void ReadDaemonConfigFromFile_NoDaemonSection_ReturnsDefaults()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");

        var result = ConfigWatcherService.ReadDaemonConfigFromFile(_paths.NetclawConfigPath);

        Assert.Equal(new DaemonConfig(), result);
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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
