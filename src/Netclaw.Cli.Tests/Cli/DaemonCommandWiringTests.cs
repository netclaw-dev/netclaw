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
    private readonly List<Process> _children = [];

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
        foreach (var child in _children)
        {
            if (!child.HasExited)
                child.Kill();
            child.Dispose();
        }

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

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
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

    /// <summary>Runs a stand-in process that <c>netclaw daemon</c> recognises as netclawd.</summary>
    private Process StartFakeDaemonProcess(string netclawHome)
    {
        Directory.CreateDirectory(netclawHome);
        var fakeDaemon = Path.Combine(_dir.Path, "netclawd");
        File.Copy("/bin/sleep", fakeDaemon, overwrite: true);
        MakeExecutable(fakeDaemon);
        var psi = new ProcessStartInfo(fakeDaemon, "600") { UseShellExecute = false };
        psi.Environment["NETCLAW_HOME"] = netclawHome; // the home this daemon serves, as the real one's environment says
        var process = Process.Start(psi)!;
        _children.Add(process);
        File.WriteAllText(Path.Combine(netclawHome, "netclaw.pid"), process.Id.ToString());

        // No lock file on purpose: a lock held by this process would be released by an Exited
        // callback that can run after the CLI has already seen the daemon exit, so the CLI would
        // still report it as running.
        return process;
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

    // ── daemon stop / start ownership ───────────────────────────────────────

    /// <summary>The unit is active and its MainPID is the daemon this home's pid file records.</summary>
    private Process UnitRunsDaemonOf(string netclawHome)
    {
        InstallUnit();
        var daemon = StartFakeDaemonProcess(netclawHome);
        FakeSystemctl("active.code", "0");
        FakeSystemctl("active.out", "active\n");
        FakeSystemctl("mainpid.out", daemon.Id + "\n");
        return daemon;
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_GoesThroughTheUnit_ForTheDefaultHome()
    {
        UnitRunsDaemonOf(DefaultNetclawHome);

        var (exitCode, output) = await RunAsync(DefaultNetclawHome, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("--user stop netclaw.service", SystemctlCalls);
        Assert.Contains("Stopped systemd user service.", output);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_GoesThroughTheUnit_ForANonDefaultHomeTheUnitServes()
    {
        // A drop-in `Environment=NETCLAW_HOME=...` makes the unit serve another home.
        UnitRunsDaemonOf(ScratchHome);

        var (exitCode, output) = await RunAsync(ScratchHome, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("--user stop netclaw.service", SystemctlCalls);
        Assert.Contains("Stopped systemd user service.", output);
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_GoesThroughTheUnit_ForASymlinkedSpellingOfTheDefaultHome()
    {
        UnitRunsDaemonOf(DefaultNetclawHome);
        var link = Path.Combine(_dir.Path, "link-to-default");
        Directory.CreateSymbolicLink(link, DefaultNetclawHome);

        var (exitCode, output) = await RunAsync(link, ["daemon", "stop"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("--user stop netclaw.service", SystemctlCalls);
        Assert.Contains("Stopped systemd user service.", output);
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
        FakeSystemctl("mainpid.out", "4242\n");
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
    }

    [SlopwatchSuppress("SW001", "Drives the Linux systemd user-service path through a fake systemctl.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Drives the Linux systemd user-service path through a fake systemctl.")]
    public async Task Stop_NeverTouchesTheUnit_WhenAnotherHomesStalePidFileNamesTheUnitsDaemon()
    {
        // F4: the pid file in the scratch home holds the PID of the default home's live daemon. The
        // unit's MainPID matches it, but that process serves the default home (its environment
        // says so), so the scratch home's stop must not go through the unit.
        var unitDaemon = UnitRunsDaemonOf(DefaultNetclawHome);
        Directory.CreateDirectory(ScratchHome);
        File.WriteAllText(Path.Combine(ScratchHome, "netclaw.pid"), unitDaemon.Id.ToString());

        var (_, output) = await RunAsync(ScratchHome, ["daemon", "stop"]);

        Assert.DoesNotContain("--user stop", SystemctlCalls);
        Assert.DoesNotContain("systemd user service", output);
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
