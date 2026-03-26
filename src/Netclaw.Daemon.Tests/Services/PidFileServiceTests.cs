using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class PidFileServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public PidFileServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public async Task StartAsync_WritesTwoLinePidFile()
    {
        var sut = CreateService();

        await sut.StartAsync(CancellationToken.None);

        Assert.True(File.Exists(_paths.PidFilePath));
        var lines = File.ReadAllLines(_paths.PidFilePath);
        Assert.Equal(2, lines.Length);
        Assert.Equal(Environment.ProcessId.ToString(), lines[0].Trim());
        Assert.True(DateTimeOffset.TryParse(lines[1].Trim(), out _));
    }

    [Fact]
    public async Task StopAsync_DeletesPidFile_WhenNoRestartRequested()
    {
        var signal = new DaemonRestartSignal();
        var sut = CreateService(signal);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(_paths.PidFilePath));
    }

    [Fact]
    public async Task StopAsync_KeepsPidFile_WhenRestartRequested()
    {
        var signal = new DaemonRestartSignal();
        var sut = CreateService(signal);

        await sut.StartAsync(CancellationToken.None);
        signal.RequestRestart();
        await sut.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(_paths.PidFilePath));
    }

    private PidFileService CreateService(DaemonRestartSignal? signal = null)
    {
        return new PidFileService(
            _paths,
            signal ?? new DaemonRestartSignal(),
            TimeProvider.System,
            NullLogger<PidFileService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { } // slopwatch-ignore: SW003 test cleanup best-effort — directory may already be gone
    }
}
