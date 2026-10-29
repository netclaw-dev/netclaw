// -----------------------------------------------------------------------
// <copyright file="DaemonCommandWiringTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Runs the built <c>netclaw daemon ...</c> commands as child processes, so Program.cs's own
/// wiring (which owner a verb goes through, the exit code, the readiness probe, the endpoint the
/// pair instructions print) is exercised rather than only the helpers it calls. <c>HOME</c> is a
/// temp directory holding a fake <c>systemctl</c> that records its arguments, so no real unit is
/// ever consulted.
/// </summary>
public sealed class DaemonCommandWiringTests : IDisposable
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    private readonly DisposableTempDir _dir = new();
    private readonly string _home;
    private readonly string _fakeBin;
    private readonly string _systemctlState;
    private readonly List<StandInDaemon> _standIns = [];

    public DaemonCommandWiringTests()
    {
        _home = Path.Combine(_dir.Path, "home");
        _fakeBin = Path.Combine(_dir.Path, "bin");
        _systemctlState = Path.Combine(_dir.Path, "systemctl");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_fakeBin);
        Directory.CreateDirectory(_systemctlState);

        if (IsLinux)
        {
            var script = Path.Combine(_fakeBin, "systemctl");
            File.WriteAllText(script, """
                #!/bin/sh
                echo "$@" >> "$FAKE_SYSTEMCTL_DIR/calls.log"
                read_file() { cat "$FAKE_SYSTEMCTL_DIR/$1" 2>/dev/null; }
                case "$*" in
                  *is-active*)
                    read_file active.out
                    read_file active.err >&2
                    exit "$(read_file active.code || echo 3)" ;;
                  *is-enabled*)
                    exit "$(read_file enabled.code || echo 1)" ;;
                  *show*)
                    printf 'MainPID=%s\nEnvironment=DOTNET_ENVIRONMENT=Production %s\n' "$(read_file mainpid.out)" "$(read_file environment.out)" ;;
                  *) exit 0 ;;
                esac
                """.Replace("\r\n", "\n"));
            MakeExecutable(script);
        }
    }

    public void Dispose()
    {
        foreach (var standIn in _standIns)
            standIn.Dispose();

        _dir.Dispose();
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string DefaultNetclawHome => Path.Combine(_home, ".netclaw");

    private string ScratchHome => Path.Combine(_dir.Path, "scratch-home");

    private string SystemctlCalls
        => File.Exists(Path.Combine(_systemctlState, "calls.log"))
            ? File.ReadAllText(Path.Combine(_systemctlState, "calls.log"))
            : string.Empty;

    private void InstallUnit()
    {
        var unitDir = Path.Combine(_home, ".config", "systemd", "user");
        Directory.CreateDirectory(unitDir);
        File.WriteAllText(Path.Combine(unitDir, "netclaw.service"), "[Service]\nExecStart=/opt/netclaw/netclawd\n");
    }

    private void FakeSystemctl(string file, string content)
        => File.WriteAllText(Path.Combine(_systemctlState, file), content);

    private async Task<(int ExitCode, string Output)> RunAsync(
        string netclawHome,
        string[] args,
        Dictionary<string, string>? extraEnvironment = null)
    {
        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(netclawHome, args, extraEnvironment);
        return (exitCode, stdout + stderr);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunWithStreamsAsync(
        string netclawHome,
        string[] args,
        Dictionary<string, string>? extraEnvironment = null)
    {
        Directory.CreateDirectory(netclawHome);
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "netclaw.dll");
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir.Path,
        };
        psi.ArgumentList.Add(cliAssembly);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("NETCLAW_", StringComparison.Ordinal)).ToList())
            psi.Environment.Remove(key);
        psi.Environment["HOME"] = _home;
        psi.Environment["NETCLAW_HOME"] = netclawHome;
        psi.Environment["NETCLAW_DAEMON_PATH"] = Path.Combine(_dir.Path, "no-such-netclawd");
        psi.Environment["PATH"] = _fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["FAKE_SYSTEMCTL_DIR"] = _systemctlState;
        foreach (var (key, value) in extraEnvironment ?? [])
            psi.Environment[key] = value;

        StandInDaemon.RequireExplicitEndpoint(netclawHome, psi.Environment);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void WriteDaemonConfig(string netclawHome, int port, string? extra = null)
    {
        Directory.CreateDirectory(Path.Combine(netclawHome, "config"));
        File.WriteAllText(
            Path.Combine(netclawHome, "config", "netclaw.json"),
            "{\"configVersion\":1,\"Daemon\":{\"Port\":" + port + extra + "}}");
    }

    /// <summary>Holds the lock file the way a running daemon does, with no PID file.</summary>
    private FileStream HoldDaemonLock(string netclawHome)
    {
        Directory.CreateDirectory(netclawHome);
        return new FileStream(
            Path.Combine(netclawHome, "netclaw.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    /// <summary>
    /// Runs a stand-in daemon that <c>netclaw daemon</c> recognises as netclawd. It also gives
    /// the home a port and a listener, so the shutdown notice reaches this test and not port 5199.
    /// </summary>
    private StandInDaemon StartFakeDaemonProcess(string netclawHome)
    {
        Directory.CreateDirectory(netclawHome);
        var standIn = StandInDaemon.Start(netclawHome, _dir.Path);
        _standIns.Add(standIn);
        return standIn;
    }

    /// <summary>A stand-in whose process environment the test sets. Its PID file and port go to <paramref name="netclawHome"/>.</summary>
    private StandInDaemon StartFakeDaemonProcessWith(
        string netclawHome, Action<IDictionary<string, string?>> configureEnvironment, string? workingDirectory = null)
    {
        Directory.CreateDirectory(netclawHome);
        var standIn = StandInDaemon.Start(netclawHome, _dir.Path, configureEnvironment, workingDirectory);
        _standIns.Add(standIn);
        return standIn;
    }

    /// <summary>Gives a home a port and a listener, with no process, so a command on it cannot reach port 5199.</summary>
    private StandInDaemon ListenForHome(string netclawHome)
    {
        Directory.CreateDirectory(netclawHome);
        var standIn = StandInDaemon.ListenOnly(netclawHome);
        _standIns.Add(standIn);
        return standIn;
    }

    // #2527 rule 1: the notice names the PID and the home of the daemon it is for, and that daemon accepts it.
    private static void AssertOneCliStopNotice(StandInDaemon daemon)
    {
        var notice = Assert.Single(daemon.ShutdownNotices);
        Assert.Equal("cli-stop", notice.Reason);
        Assert.Equal(daemon.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), notice.Pid);
        Assert.True(notice.Home is not null && StandInDaemon.SameDirectory(notice.Home, daemon.Home),
            $"The notice named home '{notice.Home}', not '{daemon.Home}'.");
        Assert.Equal(200, notice.Status);
    }

    private static HttpListener Listen(int port, Func<HttpListenerRequest, (int Status, string Body)> handler)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                var (status, body) = handler(context.Request);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });
        return listener;
    }

    [Fact]
    public async Task Update_help_resolves_the_host_context_for_the_supplied_home_without_a_daemon()
    {
        WriteDaemonConfig(ScratchHome, FreePort());
        var configPath = Path.Combine(ScratchHome, "config", "netclaw.json");
        var before = File.ReadAllText(configPath);

        var (exitCode, output) = await RunAsync(ScratchHome, ["update", "--help"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: netclaw update", output);
        Assert.Equal(before, File.ReadAllText(configPath));
        Assert.False(Directory.Exists(DefaultNetclawHome));
        Assert.Equal(string.Empty, SystemctlCalls);
    }

    // ── daemon stop / start ownership ───────────────────────────────────────

    /// <summary>The unit is active and its MainPID is the daemon this home's pid file records.</summary>
    private StandInDaemon UnitRunsDaemonOf(string netclawHome)
    {
        InstallUnit();
        var daemon = StartFakeDaemonProcess(netclawHome);
        FakeSystemctl("active.code", "0");
        FakeSystemctl("active.out", "active\n");
        FakeSystemctl("mainpid.out", daemon.Pid + "\n");
        return daemon;
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_GoesThroughTheUnit_ForTheDefaultHome()
    {
        var daemon = UnitRunsDaemonOf(DefaultNetclawHome);

        var (exitCode, output) = await RunAsync(DefaultNetclawHome, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("--user stop netclaw.service", SystemctlCalls);
        Assert.Contains("Stopped systemd user service.", output);
        AssertOneCliStopNotice(daemon);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_GoesThroughTheUnit_ForANonDefaultHomeTheUnitServes()
    {
        // A drop-in `Environment=NETCLAW_HOME=...` makes the unit serve another home.
        var daemon = UnitRunsDaemonOf(ScratchHome);

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("--user stop netclaw.service", SystemctlCalls);
        Assert.Contains("Stopped systemd user service.", output);
        AssertOneCliStopNotice(daemon);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_GoesThroughTheUnit_ForASymlinkedSpellingOfTheDefaultHome()
    {
        var daemon = UnitRunsDaemonOf(DefaultNetclawHome);
        var link = Path.Combine(_dir.Path, "link-to-default");
        Directory.CreateSymbolicLink(link, DefaultNetclawHome);

        var (exitCode, output) = await RunAsync(link, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("--user stop netclaw.service", SystemctlCalls);
        Assert.Contains("Stopped systemd user service.", output);
        AssertOneCliStopNotice(daemon);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_NeverTouchesTheUnit_WhenTheUnitRunsADifferentDaemon()
    {
        InstallUnit();
        var scratchDaemon = StartFakeDaemonProcess(ScratchHome);
        FakeSystemctl("active.code", "0");
        FakeSystemctl("active.out", "active\n");
        FakeSystemctl("mainpid.out", int.MaxValue + "\n"); // no such process: its environment is unreadable

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.True(scratchDaemon.WaitForExit(5000), output);
        AssertOneCliStopNotice(scratchDaemon);
        Assert.DoesNotContain("--user stop", SystemctlCalls);
        Assert.DoesNotContain("systemd user service", output);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_NeverTouchesTheUnit_WhenNoDaemonRunsForThisHomeButTheUnitServesAnotherOne()
    {
        // The default home runs nothing; the unit's daemon belongs to some other home.
        InstallUnit();
        FakeSystemctl("active.code", "0");
        FakeSystemctl("active.out", "active\n");
        FakeSystemctl("mainpid.out", int.MaxValue + "\n"); // no such process: its environment is unreadable
        FakeSystemctl("environment.out", "NETCLAW_HOME=/home/op/data");

        var (exitCode, output) = await RunAsync(DefaultNetclawHome, ["daemon", "stop"]);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("--user stop", SystemctlCalls);
        Assert.Contains("Daemon is not running.", output);
        Assert.DoesNotContain("Stopped systemd user service.", output);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_StopsTheUnit_WhenItCrashLoopsOnTheDefaultHomeWithNothingRunning()
    {
        // F1: the unit is activating (auto-restart, MainPID 0) and no daemon runs.
        InstallUnit();
        FakeSystemctl("active.code", "3");
        FakeSystemctl("active.out", "activating\n");
        FakeSystemctl("mainpid.out", "0\n");

        var (exitCode, output) = await RunAsync(DefaultNetclawHome, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("--user stop netclaw.service", SystemctlCalls);
        Assert.DoesNotContain("Daemon is not running.", output);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_StopsTheUnitAndTheDetachedDaemon_WhenTheUnitLoopsOnTheDefaultHomesLock()
    {
        // F2: a detached daemon holds the default home; the enabled unit keeps retrying the lock.
        var daemon = StartFakeDaemonProcess(DefaultNetclawHome);
        InstallUnit();
        FakeSystemctl("active.code", "3");
        FakeSystemctl("active.out", "activating\n");
        FakeSystemctl("mainpid.out", "0\n");

        var (exitCode, output) = await RunAsync(DefaultNetclawHome, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.True(daemon.WaitForExit(5000), output);
        Assert.Contains("--user stop netclaw.service", SystemctlCalls);
        Assert.Contains("Stopped systemd user service.", output);
        Assert.Contains("Daemon stopped", output);
        AssertOneCliStopNotice(daemon);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_NeverTouchesTheUnit_WhenAnotherHomesStalePidFileNamesTheUnitsDaemon()
    {
        // F4: the pid file in the scratch home holds the PID of the default home's live daemon. The
        // unit's MainPID matches it, but that process serves the default home (its environment
        // says so), so the scratch home's stop must not go through the unit. #2527: it must not
        // signal that process either.
        var unitDaemon = UnitRunsDaemonOf(DefaultNetclawHome);
        // The scratch home needs a port of its own, or its notice goes to port 5199.
        var scratchListener = ListenForHome(ScratchHome);
        File.WriteAllText(Path.Combine(ScratchHome, "netclaw.pid"), unitDaemon.Pid.ToString());

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "stop"]);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("--user stop", SystemctlCalls);
        Assert.DoesNotContain("systemd user service", output);
        Assert.False(unitDaemon.HasExited, output);
        Assert.Empty(unitDaemon.ShutdownNotices);
        // A notice for the scratch home, if the CLI sends one, goes to the endpoint of that home. The
        // listener has no daemon behind it, so it refuses the notice.
        Assert.All(scratchListener.ShutdownNotices, notice => Assert.Equal(409, notice.Status));
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Start_GoesThroughTheUnit_ForTheDefaultHome()
    {
        InstallUnit();
        FakeSystemctl("active.code", "3");
        FakeSystemctl("enabled.code", "0");

        var (exitCode, output) = await RunAsync(DefaultNetclawHome, ["daemon", "start"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("--user start netclaw.service", SystemctlCalls);
        Assert.Contains("Started systemd user service.", output);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    [InlineData("stop")]
    [InlineData("start")]
    public async Task StopAndStart_NeverTouchTheUnit_ForAScratchHome(string verb)
    {
        InstallUnit();
        FakeSystemctl("active.code", "0");

        var (_, output) = await RunAsync(ScratchHome, ["daemon", verb]);

        Assert.DoesNotContain("--user stop", SystemctlCalls);
        Assert.DoesNotContain("--user start", SystemctlCalls);
        Assert.DoesNotContain("systemd user service", output);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_DoesNotAskSystemdAgain_WhenTheUnitIsAlreadyDeactivating()
    {
        // ExecStop runs `netclaw daemon stop` inside the unit's own stop job. The real command
        // runner has to hand systemctl's stdout through for this to be recognised.
        var daemon = UnitRunsDaemonOf(DefaultNetclawHome);
        FakeSystemctl("active.code", "3");
        FakeSystemctl("active.out", "deactivating\n");

        var (exitCode, output) = await RunAsync(DefaultNetclawHome, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.True(daemon.WaitForExit(5000), output);
        Assert.DoesNotContain("--user stop", SystemctlCalls);
        AssertOneCliStopNotice(daemon);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Start_DoesNotSpawnADetachedDaemon_WhenOwnershipIsUnknown()
    {
        InstallUnit();
        FakeSystemctl("active.code", "1");
        FakeSystemctl("active.err", "Failed to connect to bus");

        var (exitCode, output) = await RunAsync(DefaultNetclawHome, ["daemon", "start"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Could not determine whether systemd owns the daemon lifecycle", output);
        Assert.Contains("systemd unit is installed but the user session bus is not reachable", output);
        Assert.Contains("login session", output);
        Assert.DoesNotContain("Cannot find netclawd", output);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_ExplainsTheUnreachableBus_WhenOwnershipIsUnknown()
    {
        var daemon = StartFakeDaemonProcess(DefaultNetclawHome);
        InstallUnit();
        FakeSystemctl("active.code", "1");
        FakeSystemctl("active.err", "Failed to connect to bus: No medium found");

        var (exitCode, output) = await RunAsync(DefaultNetclawHome, ["daemon", "stop"]);

        Assert.Equal(1, exitCode);
        Assert.False(daemon.HasExited);
        Assert.Contains("systemd unit is installed but the user session bus is not reachable", output);
        Assert.Contains("systemctl --user", output);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Start_GoesThroughTheUnit_ForASymlinkedSpellingOfTheDefaultHome()
    {
        InstallUnit();
        FakeSystemctl("active.code", "3");
        FakeSystemctl("enabled.code", "0");
        Directory.CreateDirectory(DefaultNetclawHome);
        var link = Path.Combine(_dir.Path, "link-to-default");
        Directory.CreateSymbolicLink(link, DefaultNetclawHome);

        var (exitCode, output) = await RunAsync(link, ["daemon", "start"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("--user start netclaw.service", SystemctlCalls);
        Assert.Contains("Started systemd user service.", output);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Start_StartsDetached_WhenNoDaemonRunsForANonDefaultHome()
    {
        InstallUnit();
        FakeSystemctl("active.code", "0");
        FakeSystemctl("mainpid.out", "4242\n");

        var (_, output) = await RunAsync(ScratchHome, ["daemon", "start"]);

        Assert.DoesNotContain("--user start", SystemctlCalls);
        Assert.Contains("Cannot find netclawd", output); // reached the detached spawn path
    }

    // ── #2527: a stop for one home never signals the daemon of another home ──

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_RefusesAndSignalsNothing_WhenThisHomesPidFileNamesTheDaemonOfAnotherHome()
    {
        var otherHome = Path.Combine(_dir.Path, "other-home");
        var other = StartFakeDaemonProcess(otherHome);
        var scratchListener = ListenForHome(ScratchHome);
        File.WriteAllText(Path.Combine(ScratchHome, "netclaw.pid"), other.Pid.ToString());

        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(ScratchHome, ["daemon", "stop"]);

        Assert.Equal(1, exitCode);
        Assert.False(other.HasExited, stdout + stderr);
        Assert.Empty(other.ShutdownNotices);
        Assert.All(scratchListener.ShutdownNotices, notice => Assert.Equal(409, notice.Status));
        Assert.True(File.Exists(Path.Combine(ScratchHome, "netclaw.pid")), "The refusal removed the PID file.");
        Assert.Contains("Nothing was stopped.", stderr, StringComparison.Ordinal);
        Assert.Contains($"PID {other.Pid}", stderr, StringComparison.Ordinal);
        Assert.Contains(ScratchHome, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Daemon stopped", stdout + stderr, StringComparison.Ordinal);
        AssertRefusalPrintedOnceOnStderr(stdout, stderr, other.Pid, ScratchHome);
        Assert.Contains($"http://127.0.0.1:{scratchListener.Port} answered for another daemon or home.", stderr, StringComparison.Ordinal);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_RefusesAndSignalsNothing_WhenThisHomesPortBelongsToTheDaemonOfAnotherHome()
    {
        // The config of the scratch home names the port of the other daemon, and its PID file names the
        // PID of that daemon. The PID matches. The home does not, so the other daemon refuses the notice.
        var otherHome = Path.Combine(_dir.Path, "other-home");
        var other = StartFakeDaemonProcess(otherHome);
        Directory.CreateDirectory(ScratchHome);
        StandInDaemon.WriteDaemonPort(ScratchHome, other.Port);
        File.WriteAllText(Path.Combine(ScratchHome, "netclaw.pid"), other.Pid.ToString());

        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(ScratchHome, ["daemon", "stop"]);

        Assert.Equal(1, exitCode);
        Assert.False(other.HasExited, stdout + stderr);
        Assert.All(other.ShutdownNotices, notice => Assert.Equal(409, notice.Status));
        Assert.Contains("Nothing was stopped.", stderr, StringComparison.Ordinal);
        Assert.Contains($"PID {other.Pid}", stderr, StringComparison.Ordinal);
        AssertRefusalPrintedOnceOnStderr(stdout, stderr, other.Pid, ScratchHome);
    }

    // S3: one writer. The first line of the refusal is on stderr once and not on stdout. The second line names
    // the recovery for a stale PID file (delete it) and for a wedged daemon of this home (kill it by hand).
    private static void AssertRefusalPrintedOnceOnStderr(string stdout, string stderr, int pid, string home)
    {
        var firstLine = $"PID {pid} is not shown to serve {home}.";
        Assert.Equal(1, Occurrences(stderr, firstLine));
        Assert.Equal(0, Occurrences(stdout, "is not shown to serve"));
        Assert.Equal(0, Occurrences(stdout, "Nothing was stopped."));
        Assert.Contains($"delete {Path.Combine(home, "netclaw.pid")}", stderr, StringComparison.Ordinal);
        Assert.Contains($"'kill {pid}'", stderr, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;

        return count;
    }

    // ── B1: the CLI reads the target as the target would read itself ────────

    private string OtherProfile => Path.Combine(_dir.Path, "other-profile");

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_RefusesAndSignalsNothing_WhenTheDaemonHasNoNetclawHomeAndItsHomeFolderIsAnotherOne()
    {
        // The review scenario. The CLI runs with HOME=<_home> and no NETCLAW_HOME of its own choice: its home is
        // the default home of the CLI. Daemon A runs from the unit: no NETCLAW_HOME, HOME=<another profile>.
        // A stale PID file in the home of the CLI names A. Nothing answers on the port of the CLI home.
        var daemon = StartFakeDaemonProcessWith(DefaultNetclawHome, environment =>
        {
            environment.Remove("NETCLAW_HOME");
            environment["HOME"] = OtherProfile;
        });
        daemon.StopListening();

        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(DefaultNetclawHome, ["daemon", "stop"]);

        Assert.Equal(1, exitCode);
        Assert.False(daemon.HasExited, "The CLI sent SIGTERM to a daemon that serves another profile. " + stdout + stderr);
        Assert.True(File.Exists(Path.Combine(DefaultNetclawHome, "netclaw.pid")), "The refusal removed the PID file.");
        AssertRefusalPrintedOnceOnStderr(stdout, stderr, daemon.Pid, DefaultNetclawHome);
        Assert.Contains("did not confirm PID", stderr, StringComparison.Ordinal);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_StopsTheDaemon_WhenItHasNoNetclawHomeAndItsOwnHomeFolderHoldsThisHome()
    {
        // The matching positive case. The daemon has no NETCLAW_HOME. The HOME of the daemon is not the HOME of
        // the CLI, and the CLI acts on <that HOME>/.netclaw through NETCLAW_HOME.
        var profileHome = Path.Combine(OtherProfile, ".netclaw");
        var daemon = StartFakeDaemonProcessWith(profileHome, environment =>
        {
            environment.Remove("NETCLAW_HOME");
            environment["HOME"] = OtherProfile;
        });
        daemon.StopListening();

        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(profileHome, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.True(daemon.WaitForExit(5000), stdout + stderr);
        Assert.Contains("Daemon stopped", stdout, StringComparison.Ordinal);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_RefusesAndSignalsNothing_WhenTheDaemonHasNeitherNetclawHomeNorHome()
    {
        var daemon = StartFakeDaemonProcessWith(DefaultNetclawHome, environment =>
        {
            environment.Remove("NETCLAW_HOME");
            environment.Remove("HOME");
        });
        daemon.StopListening();

        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(DefaultNetclawHome, ["daemon", "stop"]);

        Assert.Equal(1, exitCode);
        Assert.False(daemon.HasExited, "The CLI sent SIGTERM to a daemon that shows no home. " + stdout + stderr);
        AssertRefusalPrintedOnceOnStderr(stdout, stderr, daemon.Pid, DefaultNetclawHome);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_RefusesAndSignalsNothing_WhenTheDaemonHasARelativeNetclawHome()
    {
        // The daemon started as `NETCLAW_HOME=.netclaw-dev netclawd` in /proj-a. The CLI runs in a folder where
        // the same relative value names its own home. The CLI cannot know the folder of the daemon, so it must not
        // guess that the folder is its own.
        var cliHome = Path.Combine(_dir.Path, ".netclaw-dev");
        var projectA = Path.Combine(_dir.Path, "proj-a");
        Directory.CreateDirectory(projectA);
        var daemon = StartFakeDaemonProcessWith(
            cliHome, environment => environment["NETCLAW_HOME"] = ".netclaw-dev", projectA);
        daemon.StopListening();

        // WorkingDirectory of the CLI process is the scratch folder, so the relative value names cliHome there.
        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(
            cliHome, ["daemon", "stop"], new Dictionary<string, string> { ["NETCLAW_HOME"] = ".netclaw-dev" });

        Assert.Equal(1, exitCode);
        Assert.False(daemon.HasExited, "The CLI sent SIGTERM to a daemon with a relative NETCLAW_HOME. " + stdout + stderr);
        Assert.True(File.Exists(Path.Combine(cliHome, "netclaw.pid")), "The refusal removed the PID file.");
    }

    // ── S2 at process level ─────────────────────────────────────────────────

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_RefusesAndSignalsNothing_WhenAnOlderDaemonOfAnotherHomeAnswers200WithItsPidAndNoHome()
    {
        // The mixed-version window of `netclaw update`. The PID is right. The home is not proven, and the
        // environment of the process names another home.
        var otherHome = Path.Combine(_dir.Path, "other-home");
        var other = StartFakeDaemonProcess(otherHome);
        var scratchListener = ListenForHome(ScratchHome);
        scratchListener.AnswerAsLegacyDaemon(other.Pid);
        File.WriteAllText(Path.Combine(ScratchHome, "netclaw.pid"), other.Pid.ToString());

        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(ScratchHome, ["daemon", "stop"]);

        Assert.Equal(1, exitCode);
        Assert.False(other.HasExited, "The CLI sent SIGTERM on a 200 that proves the PID only. " + stdout + stderr);
        AssertRefusalPrintedOnceOnStderr(stdout, stderr, other.Pid, ScratchHome);
    }

    // ── S3: status and start ────────────────────────────────────────────────

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Status_SaysThePidIsNotShownToServeThisHome_AndExitsNonZero_ForTheDaemonOfAnotherHome()
    {
        var otherHome = Path.Combine(_dir.Path, "other-home");
        var other = StartFakeDaemonProcess(otherHome);
        var scratchListener = ListenForHome(ScratchHome);
        File.WriteAllText(Path.Combine(ScratchHome, "netclaw.pid"), other.Pid.ToString());

        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(ScratchHome, ["daemon", "status"]);

        Assert.Equal(1, exitCode);
        var output = stdout + stderr;
        Assert.Equal(1, Occurrences(output, $"PID {other.Pid} is not shown to serve {ScratchHome}."));
        Assert.Contains($"No daemon of this home was proven to own PID {other.Pid}.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("did not confirm", output, StringComparison.Ordinal);
        Assert.Contains($"delete {Path.Combine(ScratchHome, "netclaw.pid")}", output, StringComparison.Ordinal);
        Assert.Contains($"'kill {other.Pid}'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("running", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("health/ready", output, StringComparison.Ordinal);
        Assert.Empty(scratchListener.Requests);
        Assert.Empty(other.Requests);
        Assert.True(File.Exists(Path.Combine(ScratchHome, "netclaw.pid")), "Status removed the PID file.");
        Assert.False(other.HasExited);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Status_ReportsTheDaemonOfThisHome_AndExitsZero_WhenItIsReady()
    {
        var daemon = StartFakeDaemonProcess(ScratchHome);

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "status"]);

        Assert.Equal(0, exitCode);
        Assert.Contains($"Daemon running (PID {daemon.Pid},", output, StringComparison.Ordinal);
        Assert.DoesNotContain("is not shown to serve", output, StringComparison.Ordinal);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Start_RefusesOnStderrOnce_AndDoesNotSayAlreadyRunning_ForTheDaemonOfAnotherHome()
    {
        var otherHome = Path.Combine(_dir.Path, "other-home");
        var other = StartFakeDaemonProcess(otherHome);
        ListenForHome(ScratchHome);
        File.WriteAllText(Path.Combine(ScratchHome, "netclaw.pid"), other.Pid.ToString());

        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(ScratchHome, ["daemon", "start"]);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("already running", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Occurrences(stderr, $"Daemon was not started. PID {other.Pid} is not shown to serve {ScratchHome}."));
        Assert.Equal(0, Occurrences(stdout, "is not shown to serve"));
        Assert.Contains($"No daemon of this home was proven to own PID {other.Pid}.", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("did not confirm", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains($"delete {Path.Combine(ScratchHome, "netclaw.pid")}", stderr, StringComparison.Ordinal);
        Assert.Contains($"'kill {other.Pid}'", stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(ScratchHome, "netclaw.pid")), "Start removed the PID file.");
        Assert.False(other.HasExited);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Start_ReportsTheDaemonOfThisHomeAsAlreadyRunning()
    {
        var daemon = StartFakeDaemonProcess(ScratchHome);

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "start"]);

        Assert.Equal(1, exitCode);
        Assert.Contains($"Daemon already running (PID {daemon.Pid}).", output, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunDaemonUsageWithoutServiceActionAsync(string subcommand)
    {
        WriteDaemonConfig(ScratchHome, FreePort());
        InstallUnit();
        var configPath = Path.Combine(ScratchHome, "config", "netclaw.json");
        var unitPath = Path.Combine(_home, ".config", "systemd", "user", "netclaw.service");
        var configBefore = File.ReadAllText(configPath);
        var unitBefore = File.ReadAllText(unitPath);

        var result = await RunWithStreamsAsync(ScratchHome, ["daemon", subcommand]);

        Assert.Equal(string.Empty, SystemctlCalls);
        Assert.Equal(configBefore, File.ReadAllText(configPath));
        Assert.Equal(unitBefore, File.ReadAllText(unitPath));
        Assert.False(File.Exists(Path.Combine(ScratchHome, "netclaw.pid")));
        Assert.False(File.Exists(Path.Combine(ScratchHome, "netclaw.lock")));
        Assert.False(Directory.Exists(DefaultNetclawHome));
        return result;
    }

    [Theory]
    [InlineData("--help", true)]
    [InlineData("-h", true)]
    [InlineData("help", true)]
    [InlineData("unknown-subcommand", false)]
    public async Task Daemon_usage_returns_the_expected_exit_code_without_service_action(string subcommand, bool succeeds)
    {
        var (exitCode, stdout, stderr) = await RunDaemonUsageWithoutServiceActionAsync(subcommand);

        Assert.Contains("Usage: netclaw daemon <subcommand>", stdout, StringComparison.Ordinal);
        Assert.Contains("is not shown to serve", stdout, StringComparison.Ordinal);
        Assert.Contains("netclaw.pid", stdout, StringComparison.Ordinal);
        Assert.Contains("kill", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("pkill", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);
        if (succeeds)
            Assert.Equal(0, exitCode);
        else
            Assert.NotEqual(0, exitCode);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_SendsTheNoticeToThisHomesOwnPort_NotToTheEndpointVariable()
    {
        // NETCLAW_DAEMON_ENDPOINT can name a remote or another local daemon. The signal always goes to
        // a local PID, so the proof must come from the daemon at the port of this home.
        var daemon = StartFakeDaemonProcess(ScratchHome);
        var decoy = ListenForHome(Path.Combine(_dir.Path, "decoy-home"));

        var (exitCode, output) = await RunAsync(
            ScratchHome,
            ["daemon", "stop"],
            new Dictionary<string, string> { ["NETCLAW_DAEMON_ENDPOINT"] = $"http://127.0.0.1:{decoy.Port}" });

        Assert.Equal(0, exitCode);
        Assert.True(daemon.WaitForExit(5000), output);
        AssertOneCliStopNotice(daemon);
        Assert.Empty(decoy.Requests);
    }

    // ── daemon stop under the container supervisor ──────────────────────────

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_StopsTheDaemonAndSaysTheSupervisorRestartsIt_UnderTheContainerSupervisor()
    {
        var daemon = StartFakeDaemonProcess(ScratchHome);

        var (exitCode, output) = await RunAsync(
            ScratchHome,
            ["daemon", "stop"],
            new Dictionary<string, string> { ["NETCLAW_CONTAINER_SUPERVISOR"] = "1" });

        Assert.Equal(0, exitCode);
        Assert.True(daemon.WaitForExit(5000), output);
        Assert.Contains("The container supervisor will restart the daemon.", output);
        AssertOneCliStopNotice(daemon);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_DoesNotMentionASupervisor_OutsideAContainer()
    {
        var daemon = StartFakeDaemonProcess(ScratchHome);

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.True(daemon.WaitForExit(5000), output);
        Assert.DoesNotContain("supervisor", output);
        AssertOneCliStopNotice(daemon);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Stop_DoesNotPromiseASupervisorRestart_WhenTheStopFailed()
    {
        // Nothing runs, so the stop fails; the note about the supervisor restarting the daemon
        // is only true after a stop that happened.
        var (exitCode, output) = await RunAsync(
            ScratchHome,
            ["daemon", "stop"],
            new Dictionary<string, string> { ["NETCLAW_CONTAINER_SUPERVISOR"] = "1" });

        Assert.Equal(1, exitCode);
        Assert.Contains("Daemon is not running.", output);
        Assert.DoesNotContain("supervisor will restart", output);
    }

    // ── daemon status ───────────────────────────────────────────────────────

    [Fact]
    public async Task Status_ExitsNonZero_WhenNoDaemonIsRunning()
    {
        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "status"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Daemon is not running.", output);
    }

    [Fact]
    public async Task Status_ExitsNonZero_WhenTheProcessRunsButReadinessDoesNotAnswer()
    {
        var port = FreePort();
        WriteDaemonConfig(ScratchHome, port);
        using var lockHolder = HoldDaemonLock(ScratchHome);

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "status"]);

        Assert.Equal(1, exitCode);
        Assert.Contains($"http://127.0.0.1:{port}/api/health/ready did not answer", output);
    }

    [Fact]
    public async Task Status_PrintsOneMessage_AndExitsNonZero_WhenTheLocalConfigCannotBeRead()
    {
        Directory.CreateDirectory(Path.Combine(ScratchHome, "config"));
        File.WriteAllText(Path.Combine(ScratchHome, "config", "netclaw.json"), "{\"configVersion\":1,\"configVersion\":1}");
        using var lockHolder = HoldDaemonLock(ScratchHome);

        var (exitCode, stdout, stderr) = await RunWithStreamsAsync(ScratchHome, ["daemon", "status"]);

        Assert.Equal(1, exitCode);
        var line = Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Contains("address cannot be determined", line);
        Assert.DoesNotContain("Daemon is running", stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public async Task Status_NamesSecretsJson_WhenThatFileCannotBeRead()
    {
        Directory.CreateDirectory(Path.Combine(ScratchHome, "config"));
        File.WriteAllText(Path.Combine(ScratchHome, "config", "netclaw.json"), "{}");
        File.WriteAllText(Path.Combine(ScratchHome, "config", "secrets.json"), "{not json");
        using var lockHolder = HoldDaemonLock(ScratchHome);

        var (exitCode, stdout, _) = await RunWithStreamsAsync(ScratchHome, ["daemon", "status"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("address cannot be determined", stdout);
        Assert.Contains("secrets.json", stdout);
        Assert.DoesNotContain("netclaw.json", stdout);
    }

    [Fact]
    public async Task Status_ExitsZero_WhenTheProcessRunsAndReadinessAnswers()
    {
        var port = FreePort();
        WriteDaemonConfig(ScratchHome, port);
        using var lockHolder = HoldDaemonLock(ScratchHome);
        using var daemon = Listen(port, request =>
            request.Url!.AbsolutePath == "/api/health/ready" ? (200, "{}") : (404, "{}"));

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "status"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("netclaw status", output);
    }

    [SlopwatchSuppress("SW001", "Needs a non-root Linux user: root may chmod the stand-in directory.")]
    [Fact(SkipUnless = nameof(IsLinuxNonRoot), Skip = "Needs a non-root Linux user: root may chmod the stand-in directory.")]
    public async Task Status_DoesNotTouchKeysOrSecrets_SoItSucceedsWhenTheKeysDirectoryIsNotOurs()
    {
        // A CLI user other than the daemon's owner cannot chmod the home's keys directory, and
        // building the data-protection provider does exactly that. The keys directory is a link to a
        // root-owned directory here, which the test user cannot chmod either (the attempt fails).
        var port = FreePort();
        WriteDaemonConfig(ScratchHome, port);
        Directory.CreateSymbolicLink(Path.Combine(ScratchHome, "keys"), "/usr");
        using var lockHolder = HoldDaemonLock(ScratchHome);
        using var daemon = Listen(port, request =>
            request.Url!.AbsolutePath == "/api/health/ready" ? (200, "{}") : (404, "{}"));

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "status"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("netclaw status", output);
    }

    public static bool IsLinuxNonRoot => OperatingSystem.IsLinux() && Environment.UserName != "root";

    [Fact]
    public async Task Status_ProbesThisHomesDaemon_NotTheEndpointFromTheEnvironment()
    {
        // A home that is also paired to (or pointed at) a remote daemon must not report that
        // daemon's health as its own.
        var localPort = FreePort();
        var remotePort = FreePort();
        WriteDaemonConfig(ScratchHome, localPort);
        using var lockHolder = HoldDaemonLock(ScratchHome);
        using var remote = Listen(remotePort, _ => (200, "{}"));

        var (exitCode, output) = await RunAsync(
            ScratchHome,
            ["daemon", "status"],
            new Dictionary<string, string> { ["NETCLAW_DAEMON_ENDPOINT"] = $"http://127.0.0.1:{remotePort}" });

        Assert.Equal(1, exitCode);
        Assert.Contains($"http://127.0.0.1:{localPort}/api/health/ready did not answer", output);
    }

    // ── status ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_PrintsTheRejectedConfigReasonOnlyWhenTheDaemonReportsOne(bool rejected)
    {
        var port = FreePort();
        WriteDaemonConfig(ScratchHome, port);
        var reason = rejected
            ? ",\"configNotApplied\":\"Models:Roles:Main references unknown definition 'nope'.\""
            : string.Empty;
        using var daemon = Listen(port, _ => (200,
            "{\"overall\":\"" + (rejected ? "degraded" : "healthy") + "\"" + reason
            + ",\"build\":{\"version\":\"1\",\"commitHash\":\"c\",\"buildTimestamp\":\"t\"}"
            + ",\"process\":{\"pid\":1,\"startedAtUtc\":\"2030-01-01T00:00:00+00:00\",\"uptimeSeconds\":5}"
            + ",\"connectors\":[],\"persistence\":{\"provider\":\"Sqlite\"},\"telemetry\":{\"enabled\":false}}"));

        var (_, output) = await RunAsync(ScratchHome, ["status"]);

        Assert.Contains("overall: " + (rejected ? "degraded" : "healthy"), output);
        if (rejected)
            Assert.Contains("config on disk not applied: Models:Roles:Main references unknown definition 'nope'.", output);
        else
            Assert.DoesNotContain("config on disk not applied", output);
    }

    // ── daemon pair ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("local", true)]
    [InlineData("reverse-proxy", false)]
    public async Task Pair_PrintsTheLoopbackEndpointOnlyInLocalMode(string exposureMode, bool printsEndpoint)
    {
        var port = FreePort();
        WriteDaemonConfig(
            ScratchHome, port, ",\"Host\":\"0.0.0.0\",\"ExposureMode\":\"" + exposureMode + "\"");
        using var daemon = Listen(port, _ => (200, """{"formattedCode":"ABCD-EFGH","expiresAt":"2030-01-01T00:00:00+00:00"}"""));

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "pair"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("ABCD-EFGH", output);
        if (printsEndpoint)
        {
            Assert.Contains($"netclaw pair http://127.0.0.1:{port}", output);
        }
        else
        {
            Assert.Contains("netclaw pair <https-address>", output);
            Assert.DoesNotContain($"netclaw pair http://127.0.0.1:{port}", output);
        }
    }
}
