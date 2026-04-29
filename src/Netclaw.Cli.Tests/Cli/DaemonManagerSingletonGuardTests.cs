// -----------------------------------------------------------------------
// <copyright file="DaemonManagerSingletonGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonManagerSingletonGuardTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly DaemonManager _sut;

    public DaemonManagerSingletonGuardTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _sut = new DaemonManager(_paths, TimeProvider.System);
    }

    [Fact]
    public void IsLockFileHeld_ReturnsFalse_WhenNoLock()
    {
        Assert.False(_sut.IsLockFileHeld());
    }

    [Fact]
    public void IsLockFileHeld_ReturnsTrue_WhenLockHeld()
    {
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.True(_sut.IsLockFileHeld());
    }

    [Fact]
    public void IsLockFileHeld_ReturnsFalse_AfterLockReleased()
    {
        // Acquire and release
        using (var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.True(_sut.IsLockFileHeld());
        }

        // After release, probe should succeed
        Assert.False(_sut.IsLockFileHeld());
    }

    [Fact]
    public void GetStatus_ReportsNotRunning_WhenNoPidFileAndNoLock()
    {
        var status = _sut.GetStatus();
        Assert.False(status.IsRunning);
        Assert.Null(status.Pid);
    }

    [Fact]
    public void GetStatus_ReportsRunning_WhenLockHeldButNoPidFile()
    {
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var status = _sut.GetStatus();
        Assert.True(status.IsRunning);
        Assert.Null(status.Pid);
        Assert.Contains("PID file missing", status.Message);
    }

    [Fact]
    public void Start_RefusesToStart_WhenLockHeld()
    {
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = _sut.Start();
        Assert.False(result.Success);
        Assert.Contains("already running", result.Message);
    }

    public void Dispose()
    {
        try { _dir.Dispose(); }
        catch (IOException) { } // slopwatch-ignore: SW003 test cleanup best-effort — directory may already be gone
    }
}
