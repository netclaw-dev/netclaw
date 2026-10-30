// -----------------------------------------------------------------------
// <copyright file="SystemdUserService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text.RegularExpressions;
using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

internal enum SystemdUserServiceOwnershipKind
{
    Unmanaged,
    Managed,
    Unknown
}

internal sealed record SystemdUserServiceOwnership(
    SystemdUserServiceOwnershipKind Kind,
    string Message)
{
    public static SystemdUserServiceOwnership Managed(string message) =>
        new(SystemdUserServiceOwnershipKind.Managed, message);

    public static SystemdUserServiceOwnership Unmanaged(string message) =>
        new(SystemdUserServiceOwnershipKind.Unmanaged, message);

    public static SystemdUserServiceOwnership Unknown(string message) =>
        new(SystemdUserServiceOwnershipKind.Unknown, message);
}

/// <summary>The home a unit serves: <see cref="Path"/>, or <see cref="Problem"/> when that cannot be told.</summary>
internal sealed record UnitHome(string? Path, string? Problem);

internal sealed class SystemdUserService(
    string homePath,
    string? unitFilePath = null,
    ISystemCommandRunner? commandRunner = null,
    bool? enabledOnThisPlatform = null,
    Func<int, string?>? environReader = null)
{
    private const string ServiceName = "netclaw.service";

    private readonly string _unitFilePath = unitFilePath ?? DaemonManager.SystemdUserUnitFilePath;
    private readonly ISystemCommandRunner _commandRunner = commandRunner ?? ProcessSystemCommandRunner.Instance;
    private readonly bool _enabledOnThisPlatform = enabledOnThisPlatform ?? OperatingSystem.IsLinux();

    private readonly Func<int, string?> _readEnviron = environReader ?? HomeIdentity.ReadProcessEnvironment;

    private readonly string _homePath = homePath;

    /// <summary>
    /// Stop rule: after a stop succeeds, nothing may bring this home's daemon back. The unit is
    /// <see cref="SystemdUserServiceOwnershipKind.Managed"/> (so it must be stopped) when it is
    /// running or about to run a daemon (<c>active</c>, <c>activating</c> including auto-restart,
    /// <c>reloading</c>) and serves this home: its MainPID is this home's daemon, or, with no main
    /// process, the home that the unit names (<see cref="ReadUnitHome"/>) is this home. Any other
    /// home is never the unit's to stop on a guess. <c>deactivating</c> is left alone: that is the
    /// unit's own ExecStop re-entering.
    /// </summary>
    public async Task<SystemdUserServiceOwnership> GetStopOwnershipAsync(DaemonStatus daemonStatus)
    {
        if (PlatformOrUnitMissing() is { } skipped)
            return skipped;

        // A unit home that cannot be told only matters where nothing else settles the answer.
        var unit = ReadUnitHome(_unitFilePath);

        // Another home with nothing running for it: the unit cannot be what starts it.
        if (!daemonStatus.IsRunning && unit.Path is not null && !HomeIdentity.Same(unit.Path, _homePath))
            return SystemdUserServiceOwnership.Unmanaged(
                $"No daemon is running for {_homePath}, and netclaw.service serves {unit.Path}.");

        var active = await _commandRunner.RunAsync("systemctl", $"--user is-active {ServiceName}");
        var state = active.StandardOutput.Trim();
        if (state is not ("active" or "activating" or "reloading"))
            return StateCheckFailed(active) ?? SystemdUserServiceOwnership.Unmanaged(
                $"netclaw.service is {(state.Length == 0 ? "not running" : state)}, so it will not start a daemon.");

        var show = await _commandRunner.RunAsync("systemctl", $"--user show {ServiceName} -p MainPID");
        var mainPid = ShowValue(show, "MainPID") is { } text && int.TryParse(text, out var parsed) ? parsed : 0;

        if (mainPid != 0)
        {
            // The process's real environment is the truth, whatever its source (Environment=,
            // EnvironmentFile=). If it cannot be read, a PID match with the daemon is the best proof left.
            var serves = ProcessServesHome(mainPid, _homePath) ?? (daemonStatus.Pid == mainPid ? true : null);
            if (serves is { } known)
                return known
                    ? SystemdUserServiceOwnership.Managed($"netclaw.service runs this home's daemon (PID {mainPid}).")
                    : SystemdUserServiceOwnership.Unmanaged(
                        $"netclaw.service does not serve {_homePath} (main PID {mainPid}).");
        }

        // No main process to inspect (mid-restart or mid-crash): the unit's configuration says
        // which home it will start. Without a NETCLAW_HOME of its own it starts the default home.
        if (unit.Problem is not null)
            return SystemdUserServiceOwnership.Unknown(unit.Problem);

        if (HomeIdentity.Same(_homePath, unit.Path!))
            return SystemdUserServiceOwnership.Managed($"netclaw.service is {state} and starts {_homePath}.");

        return SystemdUserServiceOwnership.Unmanaged(
            $"netclaw.service does not serve {_homePath} (main PID {mainPid}).");
    }

    /// <summary>
    /// Start rule, consulted only when no daemon runs for this home: this home is started through
    /// the unit when the unit serves it (the NETCLAW_HOME of the unit file, absent means the default
    /// home, links resolved) and the unit is active or enabled; any other home is not.
    /// </summary>
    public async Task<SystemdUserServiceOwnership> GetStartOwnershipAsync()
    {
        if (PlatformOrUnitMissing() is { } skipped)
            return skipped;

        // A unit home that cannot be told is only reported once the unit would start something.
        var unit = ReadUnitHome(_unitFilePath);
        if (unit.Path is not null && !HomeIdentity.Same(unit.Path, _homePath))
            return SystemdUserServiceOwnership.Unmanaged(
                $"netclaw.service serves {unit.Path}, not {_homePath}.");

        var active = await _commandRunner.RunAsync("systemctl", $"--user is-active {ServiceName}");

        // The unit's ExecStop= runs `netclaw daemon stop`, so that process is part of a stop job
        // that systemd already started. Asking systemd to start the unit now would queue behind it.
        if (!active.Success && active.StandardOutput.Trim() == "deactivating")
            return SystemdUserServiceOwnership.Unmanaged("netclaw.service is already stopping.");

        if (active.Success)
            return unit.Problem is not null
                ? SystemdUserServiceOwnership.Unknown(unit.Problem)
                : SystemdUserServiceOwnership.Managed("netclaw.service is active.");

        var enabled = await _commandRunner.RunAsync("systemctl", $"--user is-enabled --quiet {ServiceName}");
        if (enabled.Success)
            return unit.Problem is not null
                ? SystemdUserServiceOwnership.Unknown(unit.Problem)
                : SystemdUserServiceOwnership.Managed("netclaw.service is enabled.");

        return StateCheckFailed(active, enabled) ?? SystemdUserServiceOwnership.Unmanaged(
            "netclaw.service is installed but neither active nor enabled.");
    }

    /// <summary>
    /// Whether the process <paramref name="pid"/> serves <paramref name="homePath"/>, from its
    /// NETCLAW_HOME (absent means the default home). <c>null</c> when its environment cannot be read.
    /// </summary>
    private bool? ProcessServesHome(int pid, string homePath)
        => _readEnviron(pid) is { } environ ? HomeIdentity.EnvironmentServesHome(environ, homePath) : null;

    /// <summary>
    /// The home that the unit file names, resolved as systemd does: the last <c>NETCLAW_HOME</c> of the
    /// <c>Environment=</c> lines, overridden by the <c>NETCLAW_HOME</c> of the <c>EnvironmentFile=</c> that
    /// the unit references (a missing file is fine). Absent or blank means the default home. Drop-ins
    /// (<c>systemctl --user edit</c>) are not read. The value is expanded as the daemon does
    /// (<see cref="PathExpansion.ExpandHome"/>); one that is still not an absolute path, and a unit that
    /// cannot be read, give a <see cref="UnitHome.Problem"/> instead of a path.
    /// </summary>
    internal static UnitHome ReadUnitHome(string unitFilePath)
    {
        string? declared = null;
        try
        {
            var lines = File.ReadAllLines(unitFilePath);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("Environment=", StringComparison.Ordinal))
                    continue;

                var match = Regex.Matches(trimmed["Environment=".Length..], "(?:^|\\s)\"?NETCLAW_HOME=([^\\s\"]*)").LastOrDefault();
                if (match is not null)
                    declared = match.Groups[1].Value;
            }

            if (DaemonPathEnvironmentFile.TryGetEnvironmentFilePath(lines, out var envFile)
                && ReadEnvironmentFileOrNull(envFile) is { } envText
                && DaemonPathEnvironmentFile.ReadLastValue(envText, "NETCLAW_HOME") is { } fromFile)
                declared = fromFile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UnitHome(null, $"Could not read netclaw.service to see which home it serves: {ex.Message}");
        }

        declared = declared?.Trim();
        if (string.IsNullOrEmpty(declared))
            return new UnitHome(DefaultHomePath, null);

        // systemd passes the value through; the daemon expands it as NetclawPaths does.
        var expanded = PathExpansion.ExpandHome(declared) ?? declared;
        if (!Path.IsPathFullyQualified(expanded))
            return new UnitHome(null, $"netclaw.service names NETCLAW_HOME={declared}, which is not an absolute path that Netclaw can compare. " +
                "Edit the unit or its EnvironmentFile to an absolute path.");

        return new UnitHome(Path.GetFullPath(expanded), null);
    }

    // systemd ignores a missing `-` environment file, a dangling link included.
    private static string? ReadEnvironmentFileOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string? ShowValue(SystemCommandResult show, string property) =>
        show.Success
            ? show.StandardOutput.Split('\n')
                .FirstOrDefault(line => line.StartsWith(property + "=", StringComparison.Ordinal))
                ?[(property.Length + 1)..].Trim()
            : null;

    private SystemdUserServiceOwnership? PlatformOrUnitMissing()
    {
        if (!_enabledOnThisPlatform)
            return SystemdUserServiceOwnership.Unmanaged("systemd user services are Linux-only.");

        return File.Exists(_unitFilePath)
            ? null
            : SystemdUserServiceOwnership.Unmanaged("No netclaw systemd user service is installed.");
    }

    private static SystemdUserServiceOwnership? StateCheckFailed(SystemCommandResult active, SystemCommandResult? enabled = null)
    {
        if (active.ExecutionError is not null || enabled?.ExecutionError is not null)
        {
            return SystemdUserServiceOwnership.Unknown(
                $"Could not execute systemctl: {active.ExecutionError ?? enabled?.ExecutionError}");
        }

        if (!string.IsNullOrWhiteSpace(active.StandardError) || !string.IsNullOrWhiteSpace(enabled?.StandardError))
        {
            return SystemdUserServiceOwnership.Unknown(
                $"Could not determine netclaw.service state: {active.Message}" +
                (enabled is null ? string.Empty : $"; {enabled.Message}") +
                ". A systemd unit is installed but the user session bus is not reachable: " +
                "run this command from a login session, or use `systemctl --user` directly.");
        }

        return null;
    }

    internal static string DefaultHomePath => HomeIdentity.DefaultHome;

    private static bool IsDefaultHome(string homePath) =>
        HomeIdentity.Same(homePath, DefaultHomePath);

    public async Task<DaemonResult> StopAsync()
    {
        var result = await _commandRunner.RunAsync("systemctl", $"--user stop {ServiceName}");
        return ToDaemonResult(result, "Stopped systemd user service.");
    }

    public async Task<DaemonResult> StartAsync()
    {
        var result = await _commandRunner.RunAsync("systemctl", $"--user start {ServiceName}");
        return ToDaemonResult(result, "Started systemd user service.");
    }

    private static DaemonResult ToDaemonResult(SystemCommandResult result, string successMessage) =>
        result.Success
            ? new DaemonResult(true, successMessage)
            : new DaemonResult(false, result.Message);
}

internal interface ISystemCommandRunner
{
    Task<SystemCommandResult> RunAsync(string command, string arguments);
}

internal sealed record SystemCommandResult(
    int ExitCode,
    string StandardError,
    string? ExecutionError = null,
    string StandardOutput = "")
{
    public bool Success => ExecutionError is null && ExitCode == 0;

    public string Message => ExecutionError
        ?? (string.IsNullOrWhiteSpace(StandardError)
            ? $"Command exited with code {ExitCode}."
            : StandardError.Trim());
}

internal sealed class ProcessSystemCommandRunner : ISystemCommandRunner
{
    public static ProcessSystemCommandRunner Instance { get; } = new();

    public async Task<SystemCommandResult> RunAsync(string command, string arguments)
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

            using var proc = Process.Start(psi);
            if (proc is null)
                return new SystemCommandResult(-1, string.Empty, $"Failed to start command '{command}'.");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            var stdout = await stdoutTask;
            await proc.WaitForExitAsync();

            return new SystemCommandResult(proc.ExitCode, stderr, StandardOutput: stdout);
        }
        catch (Exception ex)
        {
            return new SystemCommandResult(-1, string.Empty, ex.Message);
        }
    }
}
