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
    private readonly List<System.Diagnostics.Process> _fakeDaemons = [];

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
        File.WriteAllText(_paths.PidFilePath, daemon.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var status = _sut.GetStatus();

        Assert.True(status.IsRunning);
        Assert.Equal(daemon.Id, status.Pid);
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
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var requestTask = Task.Run(async () =>
        {
            var received = await listener.GetContextAsync();
            received.Response.StatusCode = 200;
            received.Response.Close();
            return received.Request.Url!.AbsolutePath;
        });
        Netclaw.Cli.Config.ClientConfigFile.WriteEndpoint(_paths, $"http://127.0.0.1:{port}");
        var daemon = StartFakeDaemon();
        File.WriteAllText(_paths.PidFilePath, daemon.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        var path = await requestTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("/api/lifecycle/shutdown", path);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private System.Diagnostics.Process StartFakeDaemon()
    {
        var fakeDaemon = Path.Combine(_dir.Path, "netclawd");
        File.Copy("/bin/sleep", fakeDaemon, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fakeDaemon, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fakeDaemon, "600") { UseShellExecute = false })!;
        _fakeDaemons.Add(process);
        return process;
    }

    private sealed class FakeSupervisor(bool supervised) : IContainerSupervisor
    {
        public bool IsExternallySupervised => supervised;
    }

    public void Dispose()
    {
        foreach (var process in _fakeDaemons)
        {
            if (!process.HasExited)
                process.Kill();
            process.Dispose();
        }

        try { _dir.Dispose(); }
        catch (IOException) { } // slopwatch-ignore: SW003 test cleanup best-effort — directory may already be gone
    }
}
