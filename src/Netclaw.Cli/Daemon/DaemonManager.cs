// -----------------------------------------------------------------------
// <copyright file="DaemonManager.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Manages the netclawd daemon process lifecycle: start, stop, status,
/// and systemd service registration (Linux only).
/// </summary>
public sealed partial class DaemonManager
{
    private readonly NetclawPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly IContainerSupervisor _supervisor;
    private readonly ISystemCommandRunner _commandRunner;
    private readonly Func<HttpClient> _createLocalControlHttpClient;

    public DaemonManager(NetclawPaths paths, TimeProvider timeProvider, IContainerSupervisor? supervisor = null)
        : this(paths, timeProvider, supervisor ?? new ContainerSupervisor(), ProcessSystemCommandRunner.Instance,
            DaemonApi.CreateLocalControlHttpClient)
    {
    }

    internal DaemonManager(
        NetclawPaths paths,
        TimeProvider timeProvider,
        IContainerSupervisor supervisor,
        ISystemCommandRunner commandRunner,
        Func<HttpClient> createLocalControlHttpClient)
    {
        ArgumentNullException.ThrowIfNull(createLocalControlHttpClient);
        _paths = paths;
        _timeProvider = timeProvider;
        _supervisor = supervisor;
        _commandRunner = commandRunner;
        _createLocalControlHttpClient = createLocalControlHttpClient;
    }

    internal string HomePath => _paths.BasePath;

    /// <summary>
    /// Starts the daemon as a detached background process.
    /// </summary>
    /// <remarks>
    /// When an external supervisor owns the lifecycle (the official Docker image,
    /// where entrypoint.sh is PID 1), this never spawns a process: a second
    /// netclawd would race the supervised one for the singleton lock file (#1279).
    /// The supervisor is responsible for (re)starting the daemon.
    /// </remarks>
    public DaemonResult Start()
    {
        if (_supervisor.IsExternallySupervised)
        {
            // Fail loudly rather than silently promising an auto-start: if the marker is
            // set but no supervisor is actually present (e.g. a derived image that kept
            // NETCLAW_CONTAINER_SUPERVISOR but replaced the entrypoint), nothing will
            // start the daemon and the operator needs to know to check the container.
            return TryGetRunningPid(out var supervisedPid)
                ? new DaemonResult(true, $"Daemon managed by container supervisor (PID {supervisedPid}).")
                : new DaemonResult(false,
                    "Daemon is not running and its startup is managed by the container supervisor "
                    + "(NETCLAW_CONTAINER_SUPERVISOR is set); this command does not start it. If it is "
                    + "not coming up, check the container/entrypoint logs — the marker may be set without "
                    + "a supervisor present.");
        }

        if (TryGetRunningPid(out var existingPid))
        {
            if (existingPid != 0 && !PidServesThisHome(existingPid))
                return new DaemonResult(
                    false,
                    $"Daemon was not started. {NotShownWithoutNotice(existingPid)}",
                    IsRefusal: true);

            return new DaemonResult(false, $"Daemon already running (PID {existingPid}).");
        }

        var binaryPath = FindDaemonBinary();
        if (binaryPath is null)
            return new DaemonResult(false,
                "Cannot find netclawd binary. Set NETCLAW_DAEMON_PATH or ensure it is " +
                "in the same directory as the CLI.");

        _paths.EnsureDirectoriesExist();

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Don't redirect stdio — holding pipe handles prevents clean CLI exit
            // and can cause the daemon to get SIGPIPE. The daemon handles its own
            // logging via the ASP.NET Core logging infrastructure.
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };

        // The child serves the home of this CLI whatever NETCLAW_HOME the CLI inherited. A later stop
        // can then show that the PID serves this home.
        startInfo.Environment["NETCLAW_HOME"] = _paths.BasePath;

        var startedAt = _timeProvider.GetUtcNow();

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Exception ex)
        {
            return new DaemonResult(false, $"Failed to start daemon: {ex.Message}");
        }

        if (process.WaitForExit(1500))
        {
            var startupFailure = TryReadStartupFailureFromCrashLog(startedAt, out var crashLogPath);
            return new DaemonResult(false, startupFailure ?? $"Failed to start daemon: process exited with code {process.ExitCode}.", crashLogPath);
        }

        // Preliminary PID file for immediate status checks. The daemon's PidFileService
        // overwrites with the authoritative two-line format. The lock file (acquired by
        // the daemon in Program.cs) is the actual singleton guard.
        File.WriteAllText(_paths.PidFilePath, process.Id.ToString());

        return new DaemonResult(true, $"Daemon started (PID {process.Id}).");
    }

    /// <summary>
    /// Stops the daemon gracefully. Posts the shutdown reason, the PID, and the home to the
    /// lifecycle endpoint of this home before sending SIGTERM, so the daemon can fire
    /// webhook notifications and (in the future) drain active sessions. The PID gets no
    /// signal until it is shown to serve this home.
    /// </summary>
    /// <param name="reason">
    /// Why the daemon is being stopped (e.g., "cli-stop", "update").
    /// Included in the shutdown webhook notification.
    /// </param>
    public async Task<DaemonResult> StopAsync(string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetRunningPid(out var pid))
            return new DaemonResult(false, "Daemon is not running.");

        if (pid == 0)
            return new DaemonResult(false,
                "Daemon appears running (lock file held) but PID file is missing. " +
                "The daemon will self-terminate shortly. If it does not, find the process that holds " +
                $"the lock file {_paths.LockFilePath} and stop that process with 'kill <pid>'.");

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // Process already gone — clean up stale PID file
            CleanupPidFile();
            return new DaemonResult(true, "Daemon was not running (stale PID file cleaned up).");
        }

        if (!IsDaemonProcess(process))
        {
            CleanupPidFile();
            return new DaemonResult(false,
                $"PID {pid} is not a netclawd process (was '{process.ProcessName}'). " +
                "Stale PID file cleaned up.");
        }

        // A PID file can name the daemon of another home (a stale file, or a port that two homes
        // share). No signal goes out until the PID is shown to serve this home. The notice does
        // double work: it tells the daemon why it stops, and the answer is the proof.
        // stop-proof:begin
        var home = HomeIdentity.Canonical(_paths.BasePath);
        var (outcome, endpoint) = await NotifyAsync(pid, home, reason, cancellationToken);
        var proven = outcome == NoticeOutcome.Accepted
            || (outcome == NoticeOutcome.Unconfirmed && HomeIdentity.ProcessServesHome(pid, home));
        if (!proven)
        {
            return new DaemonResult(
                false,
                $"Nothing was stopped. {NotShownToServe(pid, endpoint, answered: outcome == NoticeOutcome.Refused)}",
                IsRefusal: true);
        }
        // stop-proof:end

        cancellationToken.ThrowIfCancellationRequested();

        // Graceful shutdown: SIGTERM on Unix, Kill on Windows (no SIGTERM equivalent)
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (!SendSignal(pid, Signal.SIGTERM))
            {
                CleanupPidFile();
                return new DaemonResult(false, $"Failed to send SIGTERM to PID {pid}.");
            }
        }
        else
        {
            // Windows: no graceful signal available via .NET — use Kill
            process.Kill();
        }

        // Wait for graceful exit. DaemonConfig.GracefulShutdownBudget matches the daemon's own
        // Akka CoordinatedShutdown "before-service-unbind" phase timeout — where session
        // draining (SessionDrainHelper.DrainAsync) actually happens — so the CLI does not give
        // up on a daemon that is still draining in-flight sessions. See
        // DaemonConfig.GracefulShutdownBudget remarks for the full
        // layering this must respect: bounded drain < Akka phase timeout < this budget + the
        // grace window below < systemd's TimeoutStopSec= (netclaw-dev/netclaw#1664, #1665).
        var exitedWithinBudget = await WaitForExitAsync(process, DaemonConfig.GracefulShutdownBudget, cancellationToken);
        var exitedDuringGraceWindow = false;
        if (!exitedWithinBudget)
        {
            // The daemon's own bounded drain (netclaw-dev/netclaw#1664) can time out at almost
            // exactly this same budget boundary, and still needs to finish tearing down (actor
            // system termination, PID file cleanup) afterward. Poll a short additional grace
            // window before escalating to SIGKILL — production evidence (#1665) showed a
            // daemon was force-killed ~100ms from a clean exit because the CLI escalated the
            // instant its budget elapsed, with no headroom at all.
            exitedDuringGraceWindow = await WaitForExitAsync(process, DaemonConfig.CliForceKillGraceWindow, cancellationToken);
        }

        if (!exitedWithinBudget && !exitedDuringGraceWindow)
        {
            // Both the budget and the grace window elapsed — hard cutoff.
            string? killError = null;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                if (!SendSignal(pid, Signal.SIGKILL))
                    killError = "Failed to send SIGKILL.";
            }

            if (!TryKillProcess(process, out var processKillError) && string.IsNullOrWhiteSpace(killError))
                killError = processKillError;

            if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(5), cancellationToken))
            {
                var details = string.IsNullOrWhiteSpace(killError)
                    ? string.Empty
                    : $" Kill error: {killError}";

                return new DaemonResult(false,
                    $"Timed out waiting for daemon PID {pid} to exit.{details}");
            }
        }

        CleanupPidFile();

        if (exitedWithinBudget)
            return new DaemonResult(true, $"Daemon stopped (was PID {pid}).");

        if (exitedDuringGraceWindow)
        {
            // Clean exit — no kill needed — but worth surfacing: the daemon used its full
            // graceful-shutdown budget, which can mean that a session task did not stop
            // after cancellation.
            return new DaemonResult(true,
                $"Daemon stopped (was PID {pid}); exited during the " +
                $"{DaemonConfig.CliForceKillGraceWindow.TotalSeconds:F0}s grace window after the " +
                $"{DaemonConfig.GracefulShutdownBudget.TotalSeconds:F0}s graceful-wait budget elapsed " +
                "(no kill needed).");
        }

        return new DaemonResult(true,
            $"Daemon stopped (was PID {pid}), but did not exit gracefully within " +
            $"{DaemonConfig.CliForceKillBudget.TotalSeconds:F0}s (budget + grace window) and had to be " +
            "force-killed. A session task or teardown step may have stalled; if it " +
            "recurs, check for stuck sessions before stopping the daemon.");
    }

    private enum NoticeOutcome
    {
        /// <summary>A 200 whose body names the PID of the notice.</summary>
        Accepted,

        /// <summary>A 409, or a 200 for another PID. A refusal is never overruled.</summary>
        Refused,

        /// <summary>
        /// No connection, a timeout, a status other than 200 and 409, or a 200 for the right PID with no home
        /// (an older daemon). The environment of the process decides.
        /// </summary>
        Unconfirmed
    }

    // The daemon may be hung or not bound yet, so a failed notice is a normal runtime condition and
    // the stop falls back to the environment of the process. A cancel from the caller still ends the stop.
    private async Task<(NoticeOutcome Outcome, string? Endpoint)> NotifyAsync(
        int pid, string home, string reason, CancellationToken cancellationToken)
    {
        // The notice goes to the daemon of this home, never to NETCLAW_DAEMON_ENDPOINT or a client
        // config: those can name a remote daemon, and the signal goes to a local PID.
        string endpoint;
        try
        {
            endpoint = DaemonApi.ResolveLocalControlEndpoint(_paths);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                      or InvalidOperationException or FormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.Error.WriteLine($"The local daemon address is unknown. No shutdown notice was sent. {ex.Message}");
            return (NoticeOutcome.Unconfirmed, null);
        }

        using var http = _createLocalControlHttpClient();
        try
        {
            return (await SendNoticeAsync(http, endpoint, pid, home, reason, cancellationToken), endpoint);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is (HttpRequestException or TaskCanceledException))
        {
            Console.Error.WriteLine($"Note: could not notify daemon of shutdown reason: {ex.Message}");
            return (NoticeOutcome.Unconfirmed, endpoint);
        }
    }

    // stop-notice:begin
    private static async Task<NoticeOutcome> SendNoticeAsync(
        HttpClient http, string endpoint, int pid, string home, string reason, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(
            $"{endpoint}/api/lifecycle/shutdown?reason={Uri.EscapeDataString(reason)}&pid={pid}&home={Uri.EscapeDataString(home)}",
            null,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return NoticeOutcome.Refused;
        if (response.StatusCode != HttpStatusCode.OK)
            return NoticeOutcome.Unconfirmed;

        // A daemon from before the identity check answers 200 for any notice and reports its own PID, so a
        // matching PID proves the PID only. The daemon of this version also names its resolved home.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ReadAnswer(body, pid, home);
    }

    // A 200 whose body is not a daemon answer (another service on the port) is a refusal.
    private static NoticeOutcome ReadAnswer(string body, int pid, string home)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return NoticeOutcome.Refused;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("pid", out var answeredPid)
                || answeredPid.ValueKind != JsonValueKind.Number
                || !answeredPid.TryGetInt32(out var answered)
                || answered != pid)
                return NoticeOutcome.Refused;

            if (!root.TryGetProperty("home", out var answeredHome))
                return NoticeOutcome.Unconfirmed;

            // A string that is no full path is no answer: Canonical throws for an empty string or a NUL, and
            // resolves a relative string in the working folder of this CLI.
            if (answeredHome.ValueKind != JsonValueKind.String)
                return NoticeOutcome.Refused;

            var claimed = answeredHome.GetString()!;
            return HomeIdentity.IsFullPath(claimed) && HomeIdentity.Same(claimed, home)
                ? NoticeOutcome.Accepted
                : NoticeOutcome.Refused;
        }
    }
    // stop-notice:end

    // The refusal of a stop: the cause is the answer to the notice that the stop sent.
    private string NotShownToServe(int pid, string? endpoint, bool answered)
    {
        if (endpoint is null)
            return Refusal(pid, "The local daemon address is unknown. No shutdown notice was sent.");

        return Refusal(pid, answered
            ? $"The daemon at {endpoint} answered for another daemon or home."
            : $"The daemon at {endpoint} did not confirm PID {pid} for this home.");
    }

    // The refusal of start and status. They send no notice, so the cause names no endpoint and no answer.
    private string NotShownWithoutNotice(int pid)
        => Refusal(pid, $"No daemon of this home was proven to own PID {pid}.");

    // The two lines of a refusal. The second line names the cause and the recovery.
    private string Refusal(int pid, string cause)
    {
        return $"PID {pid} is not shown to serve {_paths.BasePath}.\n{cause} "
            + $"If no Netclaw daemon of this home runs, delete {_paths.PidFilePath}. "
            + $"If PID {pid} is a daemon of this home, check it, then stop it with 'kill {pid}' (Task Manager on Windows).";
    }

    // The proof of start and status. They send no notice, so they read the environment of the process, or,
    // where the environment cannot be read, the lock file of this home.
    private bool PidServesThisHome(int pid)
        => HomeIdentity.ReadProcessEnvironment(pid) is { } environment
            ? HomeIdentity.EnvironmentServesHome(environment, HomeIdentity.Canonical(_paths.BasePath))
            : IsLockFileHeld();

    /// <summary>
    /// Reports daemon status.
    /// </summary>
    public DaemonStatus GetStatus()
    {
        if (!TryGetRunningPid(out var pid))
            return new DaemonStatus(IsRunning: false, Pid: null, Message: "Daemon is not running.");

        if (pid == 0)
        {
            // Lock file is held but PID file is missing.
            // The watchdog will terminate the daemon shortly.
            return new DaemonStatus(IsRunning: true, Pid: null,
                Message: "Daemon is running (PID file missing \u2014 daemon will self-terminate shortly).");
        }

        try
        {
            var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                CleanupPidFile();
                return new DaemonStatus(IsRunning: false, Pid: null,
                    Message: "Daemon is not running (stale PID file cleaned up).");
            }

            if (!IsDaemonProcess(process))
            {
                CleanupPidFile();
                return new DaemonStatus(IsRunning: false, Pid: null,
                    Message: "Daemon is not running (PID file pointed to a different process).");
            }

            if (!PidServesThisHome(pid))
                return new DaemonStatus(IsRunning: true, Pid: pid, Message: NotShownWithoutNotice(pid), ServesThisHome: false);

            // Prefer PID file timestamp (reflects soft restarts), fall back to process start time
            TryReadPidFile(out _, out var pidFileStartedAt);
            var startedAt = pidFileStartedAt ?? new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var uptime = _timeProvider.GetUtcNow() - startedAt;
            var formattedUptime = FormatUptime(uptime);
            return new DaemonStatus(IsRunning: true, Pid: pid,
                Message: $"Daemon running (PID {pid}, uptime {formattedUptime}).", Uptime: formattedUptime);
        }
        catch (ArgumentException)
        {
            CleanupPidFile();
            return new DaemonStatus(IsRunning: false, Pid: null,
                Message: "Daemon is not running (stale PID file cleaned up).");
        }
    }

    /// <summary>
    /// Installs daemon as a systemd user service (Linux only).
    /// </summary>
    public async Task<DaemonResult> InstallAsync()
    {
        if (!OperatingSystem.IsLinux())
            return new DaemonResult(false,
                OperatingSystem.IsMacOS()
                    ? "Service installation is not yet supported on macOS — run the daemon " +
                      "manually with `netclaw daemon start`. Tracking: " +
                      "https://github.com/netclaw-dev/netclaw/issues/1015"
                    : "Service installation is currently Linux-only (systemd). " +
                      "See https://github.com/netclaw-dev/netclaw/issues for Windows service support.");

        var binaryPath = FindDaemonBinary();
        if (binaryPath is null)
            return new DaemonResult(false,
                "Cannot find netclawd binary. Set NETCLAW_DAEMON_PATH or ensure it is " +
                "in the same directory as the CLI.");

        // Probe before writing anything: a host without a reachable user systemd (a plain
        // container, WSL1, a minimal VM) would otherwise fail at daemon-reload and leave
        // the generated unit and env file behind.
        var userManager = await RunCommandAsync("systemctl", "--user show-environment");
        if (!userManager.Success)
            return new DaemonResult(false,
                "Cannot install the service: systemd user services are not available on this host " +
                $"({userManager.Message}). Run the daemon under a container supervisor, or start it " +
                "with `netclaw daemon start` (it will not restart after a crash or reboot).");

        Directory.CreateDirectory(SystemdUserUnitDirectory);

        // CLI binary is in the same directory as the daemon binary
        var installDir = Path.GetDirectoryName(binaryPath)!;
        var cliBinaryPath = Path.Combine(installDir, "netclaw");

        // A systemd --user service starts with a sanitized PATH that excludes installDir,
        // ~/.dotnet, ~/.local/bin, and everything else the operator has on their shell
        // PATH, so the agent's shell tool cannot resolve `netclaw`, `dotnet`, etc. Rather
        // than guess a directory list (which can never anticipate every environment —
        // #1544, where ~/.dotnet was invisible), capture the operator's REAL PATH from
        // this CLI process — a child of the operator's shell, so it already holds the live
        // PATH with no shell spawned — and hand it to the daemon via a netclaw-owned
        // EnvironmentFile. The daemon only ever reads that file; `doctor --fix` rehydrates
        // it and SystemdUnitPathDoctorCheck validates it. All three go through
        // DaemonPathEnvironmentFile so the contract stays in lockstep.
        var envFilePath = _paths.DaemonEnvironmentFilePath;
        var capturedPath = DaemonPathEnvironmentFile.CaptureCurrentPath();
        Directory.CreateDirectory(Path.GetDirectoryName(envFilePath)!);
        // Operator lines such as NETCLAW_HOME stay. The overwrite keeps the file mode.
        var existingEnvText = await ReadEnvironmentFileOrNullAsync(envFilePath);
        await File.WriteAllTextAsync(envFilePath, DaemonPathEnvironmentFile.Merge(existingEnvText, installDir, capturedPath));

        var unitPath = SystemdUserUnitFilePath;
        var isUpgrade = File.Exists(unitPath);
        var unitContent = BuildDaemonUnitContent(binaryPath, cliBinaryPath, envFilePath);

        await File.WriteAllTextAsync(unitPath, unitContent);

        var reload = await RunCommandAsync("systemctl", "--user daemon-reload");
        if (!reload.Success)
            return new DaemonResult(false, $"Failed to reload systemd: {reload.Message}");

        var enable = await RunCommandAsync("systemctl", "--user enable netclaw.service");
        if (!enable.Success)
            return new DaemonResult(false, $"Failed to enable service: {enable.Message}");

        // Enable lingering so user services survive logout
        var linger = await RunCommandAsync("loginctl", $"enable-linger {Environment.UserName}");
        if (!linger.Success)
            return new DaemonResult(false, $"Service installed but linger failed: {linger.Message}");

        var startMessage = $"Service installed at {unitPath}. Start with: systemctl --user start netclaw";
        if (isUpgrade)
        {
            startMessage += $"\nUnit refreshed and shell-tool PATH captured to {envFilePath} — " +
                "restart to pick up the change: systemctl --user restart netclaw.";
        }

        return new DaemonResult(true, startMessage);
    }

    // File.Exists is true for a link to a missing file or a link loop, and the read then throws.
    // A missing file has no operator lines, and the write that follows creates the link target.
    private static async Task<string?> ReadEnvironmentFileOrNullAsync(string envFilePath)
    {
        if (!File.Exists(envFilePath))
            return null;

        try
        {
            return await File.ReadAllTextAsync(envFilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Path to the systemd user-service directory (<c>~/.config/systemd/user</c>).
    /// Single source of truth for daemon install/uninstall and
    /// <see cref="SystemdUnitPathDoctorCheck"/>.
    /// </summary>
    internal static string SystemdUserUnitDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user");

    internal static string SystemdUserUnitFilePath => Path.Combine(SystemdUserUnitDirectory, "netclaw.service");

    /// <summary>
    /// Deletes the netclaw-owned PATH env file written at install so uninstall leaves no
    /// orphan behind. Idempotent. Separated from <see cref="UninstallAsync"/> (which is
    /// coupled to real <c>systemctl</c>/<c>loginctl</c> and cannot be driven in-process
    /// without mutating the developer's live service) so the deletion contract is
    /// unit-testable on its own.
    /// </summary>
    internal void RemoveDaemonEnvironmentFile()
    {
        var envFilePath = _paths.DaemonEnvironmentFilePath;
        if (File.Exists(envFilePath))
            File.Delete(envFilePath);
    }

    /// <summary>
    /// Builds the systemd <c>--user</c> unit content. The daemon's shell-tool PATH is
    /// supplied out-of-band via <c>EnvironmentFile=</c> (see
    /// <see cref="DaemonPathEnvironmentFile"/>) rather than an inline
    /// <c>Environment=PATH=</c>, so it can be captured from the operator's real
    /// environment and rehydrated by <c>doctor --fix</c> without rewriting the unit.
    /// The <c>-</c> prefix makes systemd tolerant of a missing env file: a deleted PATH
    /// file degrades tool resolution (which <c>SystemdUnitPathDoctorCheck</c> flags)
    /// rather than preventing the entire daemon from starting.
    ///
    /// <c>TimeoutStopSec=</c> is <see cref="DaemonConfig.SystemdTimeoutStopSec"/> — comfortably
    /// longer than this <see cref="StopAsync"/>'s own graceful-wait-plus-grace budget
    /// (<see cref="DaemonConfig.CliForceKillBudget"/>) so systemd never SIGKILLs the whole
    /// cgroup out from under <c>ExecStop=</c> (<c>netclaw daemon stop</c>) while that command is
    /// still legitimately waiting on the daemon's own shutdown (netclaw-dev/netclaw#1665).
    /// Existing installs only pick this up after re-running <c>netclaw daemon install</c>.
    /// </summary>
    internal static string BuildDaemonUnitContent(string binaryPath, string cliBinaryPath, string environmentFilePath) => $"""
        [Unit]
        Description=Netclaw Daemon
        After=network.target

        [Service]
        Type=simple
        ExecStart={binaryPath}
        ExecStop={cliBinaryPath} daemon stop
        TimeoutStopSec={(int)DaemonConfig.SystemdTimeoutStopSec.TotalSeconds}
        Restart=always
        RestartSec=5
        Environment=DOTNET_ENVIRONMENT=Production
        EnvironmentFile=-{environmentFilePath}

        [Install]
        WantedBy=default.target
        """;

    /// <summary>
    /// Uninstalls the systemd user service (Linux only).
    /// </summary>
    public async Task<DaemonResult> UninstallAsync()
    {
        if (!OperatingSystem.IsLinux())
            return new DaemonResult(false,
                OperatingSystem.IsMacOS()
                    ? "Service uninstallation is not yet supported on macOS — stop the daemon " +
                      "with `netclaw daemon stop`. Tracking: " +
                      "https://github.com/netclaw-dev/netclaw/issues/1015"
                    : "Service uninstallation is currently Linux-only (systemd). " +
                      "See https://github.com/netclaw-dev/netclaw/issues for Windows service support.");

        await RunCommandAsync("systemctl", "--user stop netclaw.service");
        await RunCommandAsync("systemctl", "--user disable netclaw.service");

        var unitPath = SystemdUserUnitFilePath;
        if (File.Exists(unitPath))
            File.Delete(unitPath);

        RemoveDaemonEnvironmentFile();

        await RunCommandAsync("systemctl", "--user daemon-reload");

        return new DaemonResult(true, "Service uninstalled.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Internal helpers
    // ═══════════════════════════════════════════════════════════════════════

    private string? FindDaemonBinary()
    {
        // 1. Explicit environment variable
        var envPath = Environment.GetEnvironmentVariable("NETCLAW_DAEMON_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return Path.GetFullPath(envPath);

        // 2. Same directory as CLI binary
        var cliDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (cliDir is not null)
        {
            var candidate = OperatingSystem.IsWindows()
                ? Path.Combine(cliDir, "netclawd.exe")
                : Path.Combine(cliDir, "netclawd");

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private bool TryGetRunningPid(out int pid)
    {
        // Tier 1: PID file (fast path — covers normal operation)
        // stale-pid:begin
        if (TryReadPid(out pid))
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.HasExited && IsDaemonProcess(process))
                    return true;

                CleanupPidFile();
            }
            catch (ArgumentException)
            {
                CleanupPidFile();
            }
        }
        // stale-pid:end

        // Tier 2: Lock file probe (fast, OS-backed — covers PID file loss)
        if (IsLockFileHeld())
        {
            // A daemon holds the lock but we have no (valid) PID file.
            // Signal to callers via pid == 0 that we know a daemon exists
            // but can't identify its PID.
            pid = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Probes whether the daemon lock file is currently held by another process.
    /// Returns <c>true</c> if the lock cannot be acquired (a daemon is running).
    /// The probe opens and immediately disposes the file — the CLI never holds the lock.
    /// </summary>
    internal bool IsLockFileHeld()
    {
        try
        {
            using var probe = new FileStream(
                _paths.LockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // IOException: held by the daemon. UnauthorizedAccessException: we cannot open the
            // file (daemon owned by another user, read-only home), so we cannot tell: same answer.
            return true;
        }
    }

    private static bool IsDaemonProcess(Process process)
    {
        try
        {
            // ProcessName does not include extension on any platform.
            if (string.Equals(process.ProcessName, "netclawd", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.Equals(process.ProcessName, "dotnet", StringComparison.OrdinalIgnoreCase))
                return false;

            var commandLine = TryReadProcessCommandLine(process.Id);
            if (string.IsNullOrWhiteSpace(commandLine))
                return OperatingSystem.IsWindows();

            return LooksLikeDaemonCommandLine(commandLine);
        }
        catch
        {
            return false;
        }
    }

    internal static bool LooksLikeDaemonCommandLine(string commandLine)
    {
        return commandLine.Contains("netclawd.dll", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("/netclawd", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("\\netclawd", StringComparison.OrdinalIgnoreCase)
            || commandLine.EndsWith(" netclawd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandLine.Trim(), "netclawd", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadProcessCommandLine(int pid)
    {
        if (OperatingSystem.IsLinux())
            return TryReadLinuxCommandLine(pid);

        if (OperatingSystem.IsMacOS())
            return TryReadMacOsCommandLine(pid);

        // Windows command-line inspection requires additional APIs; daemon
        // management on Windows is tracked separately in issue #21.
        return null;
    }

    private static string? TryReadLinuxCommandLine(int pid)
    {
        try
        {
            var procCmdlinePath = $"/proc/{pid}/cmdline";
            if (!File.Exists(procCmdlinePath))
                return null;

            var raw = File.ReadAllText(procCmdlinePath);
            return raw.Replace('\0', ' ').Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadMacOsCommandLine(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                Arguments = $"-p {pid} -o command=",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            return proc.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private bool TryReadPid(out int pid)
    {
        return TryReadPidFile(out pid, out _);
    }

    private bool TryReadPidFile(out int pid, out DateTimeOffset? startedAt)
    {
        pid = 0;
        startedAt = null;
        if (!File.Exists(_paths.PidFilePath))
            return false;

        var lines = File.ReadAllLines(_paths.PidFilePath);
        if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out pid))
            return false;

        if (lines.Length > 1 && DateTimeOffset.TryParse(lines[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            startedAt = ts;

        return true;
    }

    private void CleanupPidFile()
    {
        try { File.Delete(_paths.PidFilePath); }
        catch (IOException ex) { Console.Error.WriteLine($"Warning: could not remove PID file: {ex.Message}"); }
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"{uptime.Hours}h {uptime.Minutes}m";
        return $"{uptime.Minutes}m {uptime.Seconds}s";
    }

    private async Task<DaemonResult> RunCommandAsync(string command, string arguments)
    {
        var result = await _commandRunner.RunAsync(command, arguments);
        return result.Success
            ? new DaemonResult(true, "OK")
            : new DaemonResult(false, result.Message);
    }

    // POSIX signals via P/Invoke
    private enum Signal
    {
        SIGKILL = 9,
        SIGTERM = 15
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);

    private static bool SendSignal(int pid, Signal signal)
    {
        return kill(pid, (int)signal) == 0;
    }

    /// <summary>
    /// Polls <paramref name="process"/> until it exits or <paramref name="timeout"/> elapses.
    /// Internal (not private) so tests can drive the bounded graceful-shutdown wait
    /// via an injected <see cref="TimeProvider"/> without a real wall-clock sleep: the poll
    /// delay is scheduled against <see cref="_timeProvider"/> (matching this repo's virtualized-
    /// timer convention, e.g. <c>ConfigWatcherService</c>), not a bare <c>Task.Delay(ms)</c>.
    /// </summary>
    internal async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(200), _timeProvider, cancellationToken);
        }

        return process.HasExited;
    }

    private static bool TryKillProcess(Process process, out string? error)
    {
        error = null;

        if (process.HasExited)
            return true;

        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Searches for a crash log written since <paramref name="startedAt"/> and extracts
    /// the structured "Daemon startup aborted: …" line if present. Returns the failure
    /// line (or <c>null</c> if none found) and the absolute path of the most recent
    /// matching crash log via <paramref name="crashLogPath"/>. Public so callers that
    /// observe a daemon-not-ready timeout can surface the same diagnostic as the
    /// immediate-start-failure path.
    /// </summary>
    public string? TryReadStartupFailureFromCrashLog(DateTimeOffset startedAt, out string? crashLogPath)
    {
        crashLogPath = null;

        try
        {
            var logsDirectory = _paths.LogsDirectory;
            if (!Directory.Exists(logsDirectory))
                return null;

            var latestCrashLog = Doctor.CrashLogHelper
                .FindCrashLogsSince(logsDirectory, startedAt.UtcDateTime.AddSeconds(-1))
                .FirstOrDefault();

            if (latestCrashLog is null)
                return null;

            foreach (var line in File.ReadLines(latestCrashLog.FullName))
            {
                if (line.Contains("Daemon startup aborted:", StringComparison.OrdinalIgnoreCase))
                {
                    crashLogPath = latestCrashLog.FullName;
                    return line;
                }
            }

            crashLogPath = latestCrashLog.FullName;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed record DaemonResult(bool Success, string Message, string? CrashLogPath = null, bool IsRefusal = false);

public sealed record DaemonStatus(bool IsRunning, int? Pid, string Message, bool ServesThisHome = true, string? Uptime = null);
