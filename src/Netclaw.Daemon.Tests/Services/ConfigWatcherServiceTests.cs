using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigWatcherServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly DaemonRestartSignal _restartSignal;
    private readonly FakeApplicationLifetime _lifetime;
    private readonly ConfigWatcherService _sut;

    public ConfigWatcherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();

        _restartSignal = new DaemonRestartSignal();
        _lifetime = new FakeApplicationLifetime();

        _sut = new ConfigWatcherService(
            _paths,
            TimeProvider.System,
            _lifetime,
            _restartSignal,
            NullLogger<ConfigWatcherService>.Instance);
    }

    [Fact]
    public void ValidConfigChange_TriggersRestart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        File.WriteAllText(_paths.SecretsPath, """{ "ApiKeys": {} }""");

        _sut.ApplyReload();

        Assert.True(_restartSignal.RestartRequested);
        Assert.True(_lifetime.StopRequested);
    }

    [Fact]
    public void InvalidJson_DoesNotTriggerRestart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ broken json """);
        // secrets.json doesn't exist — that's fine (optional)

        _sut.ApplyReload();

        Assert.False(_restartSignal.RestartRequested);
        Assert.False(_lifetime.StopRequested);
    }

    [Fact]
    public void MissingConfigFiles_TriggersRestart()
    {
        // Both files are optional in the config chain — missing = valid
        Assert.False(File.Exists(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));

        _sut.ApplyReload();

        Assert.True(_restartSignal.RestartRequested);
        Assert.True(_lifetime.StopRequested);
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested = true;
    }
}
