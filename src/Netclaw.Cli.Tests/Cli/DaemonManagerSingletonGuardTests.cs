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
    private readonly List<StandInDaemon> _standIns = [];

    public DaemonManagerSingletonGuardTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        // Pin non-supervised so these assertions don't depend on the ambient
        // NETCLAW_CONTAINER_SUPERVISOR env var (which the official image sets, and which
        // would otherwise flip the default ContainerSupervisor when running in-image).
        _sut = new DaemonManager(_paths, TimeProvider.System, new FakeSupervisor(false));
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

    [Fact]
    public void Start_DoesNotSpawn_AndReportsManaged_WhenSupervised_AndDaemonRunning()
    {
        // A supervised daemon holds the lock; the CLI must defer to the supervisor
        // and report success rather than spawning a second netclawd (#1279).
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var supervised = new DaemonManager(_paths, TimeProvider.System, new FakeSupervisor(true));

        var result = supervised.Start();

        Assert.True(result.Success);
        Assert.Contains("managed by container supervisor", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Start_ReportsSupervisorOwnsStartup_WhenSupervised_AndNotRunning()
    {
        // No lock held, no real netclawd binary: the non-supervised path would try
        // to find/spawn the binary. The supervised path must instead defer to the
        // supervisor and never reach the spawn logic.
        var supervised = new DaemonManager(_paths, TimeProvider.System, new FakeSupervisor(true));

        var result = supervised.Start();

        Assert.False(result.Success);
        Assert.Contains("container supervisor", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cannot find netclawd", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopAsync_ProceedsToStopTheProcess_WhenSupervised()
    {
        // `netclaw daemon stop` is the only CLI way to bounce a containerised daemon: the
        // supervisor restarts it after the exit. Stop must act, not refuse: with the lock held and
        // no usable PID it reaches the same "PID file is missing" outcome as an unsupervised stop.
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var supervised = new DaemonManager(_paths, TimeProvider.System, new FakeSupervisor(true));

        var result = await supervised.StopAsync("cli-stop", CancellationToken.None);

        Assert.Contains("PID file is missing", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("supervisor", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public void GetStatus_KeepsThePidFile_WhenTheLockProbeSaysFree_ButTheProcessIsAlive()
    {
        // Where file locking is unsupported or disabled the probe always says "free". A status
        // call must not delete the pid file of a live daemon (its watchdog would shut it down).
        var daemon = StartFakeDaemon();

        var status = _sut.GetStatus();

        Assert.True(status.IsRunning);
        Assert.Equal(daemon.Pid, status.Pid);
        Assert.True(File.Exists(_paths.PidFilePath));
    }

    [Fact]
    public void IsLockFileHeld_DoesNotThrow_WhenTheLockFileCannotBeOpened()
    {
        // A directory at the lock path makes the open throw UnauthorizedAccessException, as a
        // file owned by another user or a read-only home does.
        Directory.CreateDirectory(_paths.LockFilePath);

        var status = _sut.GetStatus();

        Assert.True(_sut.IsLockFileHeld());
        Assert.True(status.IsRunning);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_SendsTheShutdownNoticeToThisHomesEndpoint()
    {
        // The notice must go to the daemon of the home the manager was built on, never to the
        // default home's endpoint, or a test (or any non-default home) shuts down someone else's daemon.
        // The stand-in gives this home its own port and listener, so the notice cannot reach port 5199.
        var daemon = StartFakeDaemon();

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        var notice = Assert.Single(daemon.ShutdownNotices);
        Assert.Equal("POST", notice.Method);
        Assert.Equal("cli-stop", notice.Reason);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private StandInDaemon StartFakeDaemon()
    {
        var standIn = StandInDaemon.Start(_paths.BasePath, _dir.Path);
        _standIns.Add(standIn);
        return standIn;
    }

    private sealed class FakeSupervisor(bool supervised) : IContainerSupervisor
    {
        public bool IsExternallySupervised => supervised;
    }

    public void Dispose()
    {
        foreach (var standIn in _standIns)
            standIn.Dispose();

        try { _dir.Dispose(); }
        catch (IOException) { } // slopwatch-ignore: SW003 test cleanup best-effort — directory may already be gone
    }
}
