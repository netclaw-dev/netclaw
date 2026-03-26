using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class PidFileWatchdogServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly FakeApplicationLifetime _lifetime;

    public PidFileWatchdogServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
        _lifetime = new FakeApplicationLifetime();
    }

    [Fact]
    public async Task DoesNotShutdown_WhenPidFileExists()
    {
        // Write a PID file so the watchdog sees it
        File.WriteAllText(_paths.PidFilePath, Environment.ProcessId.ToString());

        var sut = CreateService();
        await sut.StartAsync(CancellationToken.None);

        // Wait longer than one poll interval
        await Task.Delay(PidFileWatchdogService.PollInterval + TimeSpan.FromSeconds(1));

        Assert.False(_lifetime.StopRequested);

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task InitiatesShutdown_WhenPidFileDeleted()
    {
        // Write a PID file, start watchdog, then delete the file
        File.WriteAllText(_paths.PidFilePath, Environment.ProcessId.ToString());

        var sut = CreateService();
        await sut.StartAsync(CancellationToken.None);

        // Delete the PID file
        File.Delete(_paths.PidFilePath);

        // Wait for the watchdog to detect the deletion (up to 2 poll intervals)
        var deadline = DateTime.UtcNow + PidFileWatchdogService.PollInterval * 3;
        while (!_lifetime.StopRequested && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.True(_lifetime.StopRequested);

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task InitiatesShutdown_WhenPidFileNeverExisted()
    {
        // Don't write a PID file — watchdog should detect on first poll
        var sut = CreateService();
        await sut.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow + PidFileWatchdogService.PollInterval * 3;
        while (!_lifetime.StopRequested && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.True(_lifetime.StopRequested);

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    private PidFileWatchdogService CreateService()
    {
        return new PidFileWatchdogService(
            _paths,
            _lifetime,
            NullLogger<PidFileWatchdogService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
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
