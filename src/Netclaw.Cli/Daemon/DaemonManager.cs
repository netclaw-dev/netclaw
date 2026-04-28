// -----------------------------------------------------------------------
// <copyright file="DaemonManager.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public DaemonManager(NetclawPaths paths, TimeProvider timeProvider)
    {
        _paths = paths;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Starts the daemon as a detached background process.
    /// </summary>
    public DaemonResult Start()
    {
        if (TryGetRunningPid(out var existingPid))
            return new DaemonResult(false, $"Daemon already running (PID {existingPid}).");

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

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Exception ex)
        {
            return new DaemonResult(false, $"Failed to start daemon: {ex.Message}");
        }

        // Preliminary PID file for immediate status checks. The daemon's PidFileService
        // overwrites with the authoritative two-line format. The lock file (acquired by
        // the daemon in Program.cs) is the actual singleton guard.
        File.WriteAllText(_paths.PidFilePath, process.Id.ToString());

        return new DaemonResult(true, $"Daemon started (PID {process.Id}).");
    }

    /// <summary>
    /// Stops the daemon gracefully. Posts the shutdown reason to the daemon's
    /// lifecycle endpoint before sending SIGTERM, so the daemon can fire
    /// webhook notifications and (in the future) drain active sessions.
    /// </summary>
    /// <param name="reason">
    /// Why the daemon is being stopped (e.g., "cli-stop", "update").
    /// Included in the shutdown webhook notification.
    /// </param>
    public async Task<DaemonResult> StopAsync(string reason)
    {
        if (!TryGetRunningPid(out var pid))
            return new DaemonResult(false, "Daemon is not running.");

        if (pid == 0)
            return new DaemonResult(false,
                "Daemon appears running (lock file held) but PID file is missing. " +
                "The daemon will self-terminate shortly, or use 'pkill netclawd' to stop it manually.");

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

        // Notify the daemon of the shutdown reason before sending the signal.
        // Best-effort — the daemon may already be unreachable.
        try
        {
            var endpoint = DaemonApi.ResolveEndpoint(new NetclawPaths());
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await http.PostAsync(
                $"{endpoint}/api/lifecycle/shutdown?reason={Uri.EscapeDataString(reason)}",
                null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Note: could not notify daemon of shutdown reason: {ex.Message}");
        }

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

        // Wait up to 10 seconds for graceful exit.
        if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(10)))
        {
            // Timed out — hard cutoff.
            string? killError = null;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                if (!SendSignal(pid, Signal.SIGKILL))
                    killError = "Failed to send SIGKILL.";
            }

            if (!TryKillProcess(process, out var processKillError) && string.IsNullOrWhiteSpace(killError))
                killError = processKillError;

            if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(5)))
            {
                var details = string.IsNullOrWhiteSpace(killError)
                    ? string.Empty
                    : $" Kill error: {killError}";

                return new DaemonResult(false,
                    $"Timed out waiting for daemon PID {pid} to exit.{details}");
            }
        }

        CleanupPidFile();
        return new DaemonResult(true, $"Daemon stopped (was PID {pid}).");
    }

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

            // Prefer PID file timestamp (reflects soft restarts), fall back to process start time
            TryReadPidFile(out _, out var pidFileStartedAt);
            var startedAt = pidFileStartedAt ?? new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var uptime = _timeProvider.GetUtcNow() - startedAt;
            return new DaemonStatus(IsRunning: true, Pid: pid,
                Message: $"Daemon running (PID {pid}, uptime {FormatUptime(uptime)}).");
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
                "Service installation is currently Linux-only (systemd). " +
                "See https://github.com/stannardlabs/netclaw/issues for Windows service support.");

        var binaryPath = FindDaemonBinary();
        if (binaryPath is null)
            return new DaemonResult(false,
                "Cannot find netclawd binary. Set NETCLAW_DAEMON_PATH or ensure it is " +
                "in the same directory as the CLI.");

        var unitDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user");
        Directory.CreateDirectory(unitDir);

        // CLI binary is in the same directory as the daemon binary
        var installDir = Path.GetDirectoryName(binaryPath)!;
        var cliBinaryPath = Path.Combine(installDir, "netclaw");

        var unitPath = Path.Combine(unitDir, "netclaw.service");
        var unitContent = $"""
            [Unit]
            Description=Netclaw Daemon
            After=network.target

            [Service]
            Type=simple
            ExecStart={binaryPath}
            ExecStop={cliBinaryPath} daemon stop
            Restart=always
            RestartSec=5
            Environment=DOTNET_ENVIRONMENT=Production

            [Install]
            WantedBy=default.target
            """;

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

        return new DaemonResult(true,
            $"Service installed at {unitPath}. Start with: systemctl --user start netclaw");
    }

    /// <summary>
    /// Uninstalls the systemd user service (Linux only).
    /// </summary>
    public async Task<DaemonResult> UninstallAsync()
    {
        if (!OperatingSystem.IsLinux())
            return new DaemonResult(false,
                "Service uninstallation is currently Linux-only (systemd). " +
                "See https://github.com/stannardlabs/netclaw/issues for Windows service support.");

        await RunCommandAsync("systemctl", "--user stop netclaw.service");
        await RunCommandAsync("systemctl", "--user disable netclaw.service");

        var unitPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user", "netclaw.service");

        if (File.Exists(unitPath))
            File.Delete(unitPath);

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
        catch (IOException)
        {
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

    private static async Task<DaemonResult> RunCommandAsync(string command, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)!;
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return proc.ExitCode == 0
                ? new DaemonResult(true, "OK")
                : new DaemonResult(false, stderr.Trim());
        }
        catch (Exception ex)
        {
            return new DaemonResult(false, ex.Message);
        }
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

    private async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            if (process.HasExited)
                return true;

            await Task.Delay(200);
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
}

public sealed record DaemonResult(bool Success, string Message);

public sealed record DaemonStatus(bool IsRunning, int? Pid, string Message);
