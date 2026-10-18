// -----------------------------------------------------------------------
// <copyright file="DaemonManagerSingletonGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Globalization;
using System.Net;
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
        // #2527 rule 1: the notice names the PID from the PID file and the home, so the daemon can
        // refuse a notice that is for another daemon.
        var daemon = StartFakeDaemon();

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        var notice = Assert.Single(daemon.ShutdownNotices);
        Assert.Equal("POST", notice.Method);
        Assert.Equal("cli-stop", notice.Reason);
        Assert.Equal(daemon.Pid.ToString(CultureInfo.InvariantCulture), notice.Pid);
        Assert.True(notice.Home is not null && StandInDaemon.SameDirectory(notice.Home, _paths.BasePath),
            $"The notice named home '{notice.Home}', not '{_paths.BasePath}'.");
        Assert.Equal(200, notice.Status);
        Assert.True(daemon.WaitForExit(5000), "The daemon that serves this home got no signal.");
    }

    // ── #2527 rule 2: the signal needs proof that the PID serves this home ───

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_SendsNoSignal_WhenThePidFileNamesADaemonOfAnotherHome()
    {
        // NETCLAW_HOME=/tmp/x netclaw daemon stop, where /tmp/x/netclaw.pid holds the PID of the
        // daemon of another home. Before the fix, the stand-in of the other home got SIGTERM.
        var other = StartFakeDaemonForAnotherHome();
        var thisHomePort = ListenForThisHome().Port;
        File.WriteAllText(_paths.PidFilePath, other.Pid.ToString(CultureInfo.InvariantCulture));

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        AssertRefusedToStop(result, other, Answered, thisHomePort);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_SendsNoSignal_WhenThePortOfThisHomeBelongsToTheDaemonOfAnotherHome()
    {
        // The config of this home names the port of the other daemon. That daemon knows its own PID,
        // so it matches the PID of the notice, but it refuses the home.
        var other = StartFakeDaemonForAnotherHome();
        StandInDaemon.WriteDaemonPort(_paths.BasePath, other.Port);
        File.WriteAllText(_paths.PidFilePath, other.Pid.ToString(CultureInfo.InvariantCulture));

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        AssertRefusedToStop(result, other, Answered, other.Port);
        Assert.All(other.ShutdownNotices, notice => Assert.Equal(409, notice.Status));
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_SendsNoSignal_WhenTheDaemonOfThisHomeAnswersWithAnotherPid()
    {
        // A daemon from before #2527 ignores pid and home and answers 200 with its own PID. A 200
        // is proof only when the PID in the body is the PID from the PID file.
        var other = StartFakeDaemonForAnotherHome();
        var thisHome = ListenForThisHome();
        thisHome.AnswerAsLegacyDaemon(claimedPid: other.Pid + 1);
        File.WriteAllText(_paths.PidFilePath, other.Pid.ToString(CultureInfo.InvariantCulture));

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        AssertRefusedToStop(result, other, Answered, thisHome.Port);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_SendsNoSignal_WhenNothingAnswersAndTheProcessServesAnotherHome()
    {
        // No answer from this home's port gives no proof from the daemon. The environment of the
        // process (NETCLAW_HOME) is the other source, and it names the other home.
        var other = StartFakeDaemonForAnotherHome();
        var silent = ListenForThisHome();
        silent.StopListening();
        File.WriteAllText(_paths.PidFilePath, other.Pid.ToString(CultureInfo.InvariantCulture));

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        AssertRefusedToStop(result, other, NotConfirmed, silent.Port);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_StillStopsAHungDaemon_WhenNothingAnswersButTheProcessServesThisHome()
    {
        // Regression guard for the wedged daemon: it holds the PID of this home and does not answer.
        // The environment of the process proves the home, so the stop must still work.
        var daemon = StartFakeDaemon();
        daemon.StopListening();

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.True(daemon.WaitForExit(5000));
        Assert.False(File.Exists(_paths.PidFilePath));
    }

    // ── S2: a 200 is proof of the PID and of the home ────────────────────────

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_SendsNoSignal_WhenA200NamesTheRightPidButAnotherHome()
    {
        // The environment of the process names this home, so a weak rule would stop it. A refusal is never
        // overruled by the environment.
        var daemon = StartFakeDaemon();
        daemon.AnswerAsDaemonOf(daemon.Pid, Path.Combine(_dir.Path, "other-home"));

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.False(result.Success, result.Message);
        Assert.False(daemon.HasExited, "The daemon got a signal.");
        Assert.True(File.Exists(_paths.PidFilePath), "The refusal removed the PID file.");
        Assert.Contains("answered for another daemon or home", result.Message, StringComparison.Ordinal);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_LetsTheEnvironmentDecide_WhenA200HasNoHome_AndTheProcessServesThisHome()
    {
        // A daemon from before this change answers 200 with its PID and no home. The environment of the process
        // names this home, so the stop goes on.
        var daemon = StartFakeDaemon();
        daemon.AnswerAsLegacyDaemon(daemon.Pid);

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.True(daemon.WaitForExit(5000), "The daemon that serves this home got no signal.");
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_SendsNoSignal_WhenA200HasTheRightPidAndNoHome_AndTheProcessServesAnotherHome()
    {
        // The mixed-version window: a new CLI, an old daemon of home A on a port that home B shares, and a stale
        // PID file of home B that holds the PID of that daemon. The PID matches. The home is not proven.
        var other = StartFakeDaemonForAnotherHome();
        var thisHome = ListenForThisHome();
        thisHome.AnswerAsLegacyDaemon(other.Pid);
        File.WriteAllText(_paths.PidFilePath, other.Pid.ToString(CultureInfo.InvariantCulture));

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.False(result.Success, result.Message);
        Assert.False(other.HasExited, "The daemon of the other home got a signal.");
        Assert.True(File.Exists(_paths.PidFilePath), "The refusal removed the PID file.");
        Assert.Contains($"did not confirm PID {other.Pid} for this home.", result.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> BodiesThatAreNoDaemonAnswer() => new()
    {
        { """{"pid":"@PID"}""" },
        { """{"pid":null}""" },
        { """{"pid":true}""" },
        { """{"pid":@PID.5}""" },
        { """{"pid":[@PID]}""" },
        { """{"pid":@PID,"home":42}""" },
        { """{"pid":@PID,"home":null}""" },
        { """{"pid":@PID,"home":["@HOME"]}""" },
        { """{"reason":"cli-stop"}""" },
        { """[@PID]""" },
        { "\"@PID\"" },
        { "not json" },
        { "" },
    };

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [MemberData(nameof(BodiesThatAreNoDaemonAnswer))]
    public async Task StopAsync_SendsNoSignalAndDoesNotFail_WhenA200BodyIsNoDaemonAnswer(string body)
    {
        // N2: JsonElement.TryGetInt32 throws for a string. The stop must refuse, not crash. The environment
        // names this home, so a stop that falls through to it would signal the process.
        var daemon = StartFakeDaemon();
        daemon.AnswerEveryNoticeWithBody(
            HttpStatusCode.OK,
            body.Replace("@PID", daemon.Pid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@HOME", _paths.BasePath, StringComparison.Ordinal));

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.False(result.Success, result.Message);
        Assert.True(IsRefusal(result), "The refusal is not marked, so a caller may print it twice or not at all.");
        Assert.Contains("Nothing was stopped.", result.Message, StringComparison.Ordinal);
        Assert.False(daemon.HasExited, "The daemon got a signal.");
        Assert.True(File.Exists(_paths.PidFilePath), "The refusal removed the PID file.");
    }

    // ── B1: the environment of the target decides ────────────────────────────

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_StopsAHungDaemon_WhenItHasNoNetclawHomeAndItsHomeFolderHoldsThisHome()
    {
        // The systemd unit sets no NETCLAW_HOME. The daemon serves $HOME/.netclaw, and HOME is the one of the
        // daemon, which differs from the HOME of this test process.
        var profile = Path.Combine(_dir.Path, "profile");
        var home = Path.Combine(profile, ".netclaw");
        Directory.CreateDirectory(home);
        var daemon = StartFakeDaemonWith(home, environment =>
        {
            environment.Remove("NETCLAW_HOME");
            environment["HOME"] = profile;
        });
        daemon.StopListening();

        var result = await ManagerFor(home).StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.True(daemon.WaitForExit(5000), "The daemon that serves this home got no signal.");
    }

    public static TheoryData<string> EnvironmentsOfADaemonThatServesAnotherHome() => new()
    {
        { "other-profile" },
        { "no-home-variable" },
        { "home-token" },
        { "relative-home" },
    };

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [MemberData(nameof(EnvironmentsOfADaemonThatServesAnotherHome))]
    public async Task StopAsync_SendsNoSignal_WhenNothingAnswersAndTheEnvironmentOfTheProcessIsNotShownToServeThisHome(
        string environmentKind)
    {
        var profile = Path.Combine(_dir.Path, "profile");
        var home = Path.Combine(profile, ".netclaw");
        Directory.CreateDirectory(home);
        var elsewhere = Path.Combine(_dir.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var daemon = StartFakeDaemonWith(home, environment =>
        {
            switch (environmentKind)
            {
                case "other-profile":
                    environment.Remove("NETCLAW_HOME");
                    environment["HOME"] = Path.Combine(_dir.Path, "another-profile");
                    break;
                case "no-home-variable":
                    environment.Remove("NETCLAW_HOME");
                    environment.Remove("HOME");
                    break;
                case "home-token":
                    environment["NETCLAW_HOME"] = "~/.netclaw";
                    environment["HOME"] = profile;
                    break;
                default:
                    environment["NETCLAW_HOME"] = Path.Combine("..", "profile", ".netclaw");
                    environment["HOME"] = profile;
                    break;
            }
        }, workingDirectory: elsewhere);
        daemon.StopListening();

        var result = await ManagerFor(home).StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.False(result.Success, result.Message);
        Assert.Contains("Nothing was stopped.", result.Message, StringComparison.Ordinal);
        Assert.False(daemon.HasExited, $"The daemon got a signal ({environmentKind}).");
        Assert.True(File.Exists(Path.Combine(home, "netclaw.pid")), "The refusal removed the PID file.");
    }

    // ── S3: the refusal has a cause and a recovery ───────────────────────────

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task TheRefusalHasTwoLines_AndTheSecondLineNamesTheRecovery()
    {
        var other = StartFakeDaemonForAnotherHome();
        var thisHome = ListenForThisHome();
        File.WriteAllText(_paths.PidFilePath, other.Pid.ToString(CultureInfo.InvariantCulture));

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        var lines = result.Message.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal($"Nothing was stopped. PID {other.Pid} is not shown to serve {_paths.BasePath}.", lines[0]);
        Assert.Equal(SecondLine(Answered, thisHome.Port, other.Pid), lines[1]);
    }

    // ── S3: status and start do not claim a daemon without proof ─────────────

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public void GetStatus_DoesNotReportADaemonOfAnotherHomeAsRunning()
    {
        var other = StartFakeDaemonForAnotherHome();
        var thisHome = ListenForThisHome();
        File.WriteAllText(_paths.PidFilePath, other.Pid.ToString(CultureInfo.InvariantCulture));

        var status = _sut.GetStatus();

        var lines = status.Message.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal($"PID {other.Pid} is not shown to serve {_paths.BasePath}.", lines[0]);
        Assert.Equal(SecondLine(NotConfirmed, thisHome.Port, other.Pid), lines[1]);
        Assert.DoesNotContain("running", status.Message, StringComparison.OrdinalIgnoreCase);
        // The update flow and the factory reset test IsRunning. They must go on to the stop, which refuses.
        Assert.True(status.IsRunning);
        Assert.True(File.Exists(_paths.PidFilePath), "Status removed the PID file.");
        Assert.False(other.HasExited);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public void GetStatus_ReportsTheDaemonOfThisHomeAsRunning()
    {
        var daemon = StartFakeDaemon();

        var status = _sut.GetStatus();

        Assert.True(status.IsRunning);
        Assert.Equal(daemon.Pid, status.Pid);
        Assert.StartsWith($"Daemon running (PID {daemon.Pid},", status.Message, StringComparison.Ordinal);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public void Start_DoesNotSayThatTheDaemonOfAnotherHomeIsAlreadyRunning()
    {
        var other = StartFakeDaemonForAnotherHome();
        var thisHome = ListenForThisHome();
        File.WriteAllText(_paths.PidFilePath, other.Pid.ToString(CultureInfo.InvariantCulture));

        var result = _sut.Start();

        Assert.False(result.Success);
        Assert.True(IsRefusal(result), "The refusal is not marked, so a caller may print it twice or not at all.");
        Assert.DoesNotContain("already running", result.Message, StringComparison.OrdinalIgnoreCase);
        var lines = result.Message.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal($"Daemon was not started. PID {other.Pid} is not shown to serve {_paths.BasePath}.", lines[0]);
        Assert.Equal(SecondLine(NotConfirmed, thisHome.Port, other.Pid), lines[1]);
        Assert.True(File.Exists(_paths.PidFilePath), "Start removed the PID file.");
        Assert.False(other.HasExited);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public void Start_ReportsADaemonOfThisHomeAsAlreadyRunning()
    {
        var daemon = StartFakeDaemon();

        var result = _sut.Start();

        Assert.False(result.Success);
        Assert.Equal($"Daemon already running (PID {daemon.Pid}).", result.Message);
    }

    // ── N8: no advice that stops the daemon of every home ────────────────────

    [Fact]
    public async Task StopAsync_AdvisesAStopThatNamesThisHome_WhenTheLockIsHeldAndThePidFileIsMissing()
    {
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("PID file is missing", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pkill", result.Message, StringComparison.Ordinal);
        Assert.Contains(_paths.LockFilePath, result.Message, StringComparison.Ordinal);
        Assert.Contains("kill <pid>", result.Message, StringComparison.Ordinal);
    }

    // ── a stale PID file keeps its handling (spec: daemon-lifecycle) ─────────

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task StopAsync_RemovesTheStalePidFileAndReportsNotRunning_WhenNoProcessHasThePid()
    {
        var daemon = StartFakeDaemon();
        daemon.Process!.Kill();
        Assert.True(daemon.WaitForExit(5000));

        var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("Daemon is not running.", result.Message);
        Assert.False(File.Exists(_paths.PidFilePath));
        Assert.Empty(daemon.ShutdownNotices);
    }

    [SlopwatchSuppress("SW001", "Starts a Linux utility as a process that is not a daemon.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Starts a Linux utility as a process that is not a daemon.")]
    public async Task StopAsync_RemovesThePidFileAndSignalsNothing_WhenThePidIsNotANetclawdProcess()
    {
        ListenForThisHome();
        var psi = new ProcessStartInfo("tail") { UseShellExecute = false };
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("/dev/null");
        using var notADaemon = Process.Start(psi)!;
        try
        {
            File.WriteAllText(_paths.PidFilePath, notADaemon.Id.ToString(CultureInfo.InvariantCulture));

            var result = await _sut.StopAsync("cli-stop", TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal("Daemon is not running.", result.Message);
            Assert.False(File.Exists(_paths.PidFilePath));
            Assert.False(notADaemon.HasExited);
        }
        finally
        {
            if (!notADaemon.HasExited)
                notADaemon.Kill();
        }
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private const string Answered = "answered for another daemon or home.";
    private const string NotConfirmed = "did not confirm PID {0} for this home.";

    // The exact second line of a refusal (spec: daemon-lifecycle, "A refusal tells the operator how to recover").
    private string SecondLine(string cause, int endpointPort, int pid)
        => $"The daemon at http://127.0.0.1:{endpointPort} {string.Format(CultureInfo.InvariantCulture, cause, pid)} "
           + $"If no Netclaw daemon of this home runs, delete {_paths.PidFilePath}. "
           + $"If PID {pid} is a daemon of this home, check it, then stop it with 'kill {pid}' (Task Manager on Windows).";

    private StandInDaemon StartFakeDaemon()
    {
        var standIn = StandInDaemon.Start(_paths.BasePath, _dir.Path);
        _standIns.Add(standIn);
        return standIn;
    }

    /// <summary>A stand-in whose process environment the test sets. It serves <paramref name="home"/>.</summary>
    private StandInDaemon StartFakeDaemonWith(
        string home, Action<IDictionary<string, string?>> configureEnvironment, string? workingDirectory = null)
    {
        var standIn = StandInDaemon.Start(home, _dir.Path, configureEnvironment, workingDirectory);
        _standIns.Add(standIn);
        return standIn;
    }

    private static DaemonManager ManagerFor(string home)
        => new(new NetclawPaths(home), TimeProvider.System, new FakeSupervisor(false));

    /// <summary>A stand-in for the daemon of a home that is not the home of <see cref="_sut"/>.</summary>
    private StandInDaemon StartFakeDaemonForAnotherHome()
    {
        var otherHome = Path.Combine(_dir.Path, "other-home");
        Directory.CreateDirectory(otherHome);
        var standIn = StandInDaemon.Start(otherHome, _dir.Path);
        _standIns.Add(standIn);
        return standIn;
    }

    /// <summary>
    /// Gives this home a port and a listener with no process, so a notice from this home cannot
    /// reach port 5199.
    /// </summary>
    private StandInDaemon ListenForThisHome()
    {
        var standIn = StandInDaemon.ListenOnly(_paths.BasePath);
        _standIns.Add(standIn);
        return standIn;
    }

    // #2527 rule 2: no signal, nothing removed, and a message that names the PID and this home. S3: the second
    // line names the cause and how to recover.
    private void AssertRefusedToStop(DaemonResult result, StandInDaemon other, string cause, int endpointPort)
    {
        Assert.False(result.Success, result.Message);
        Assert.True(IsRefusal(result), "The refusal is not marked, so a caller may print it twice or not at all.");
        Assert.Contains("Nothing was stopped.", result.Message, StringComparison.Ordinal);
        Assert.Contains($"PID {other.Pid}", result.Message, StringComparison.Ordinal);
        Assert.Contains(_paths.BasePath, result.Message, StringComparison.Ordinal);
        var lines = result.Message.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(SecondLine(cause, endpointPort, other.Pid), lines[1]);
        Assert.False(other.HasExited, "The daemon of the other home got a signal.");
        Assert.True(File.Exists(_paths.PidFilePath), "The refusal removed the PID file.");
        Assert.DoesNotContain(other.ShutdownNotices, notice => notice.Status == 200);
    }

    // The refusal flag of the result (DaemonResult.IsRefusal). Read by name so that this file builds before the
    // flag exists: a missing flag is false, and the test then fails for the right reason.
    private static bool IsRefusal(DaemonResult result)
        => typeof(DaemonResult).GetProperty("IsRefusal")?.GetValue(result) is true;

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
