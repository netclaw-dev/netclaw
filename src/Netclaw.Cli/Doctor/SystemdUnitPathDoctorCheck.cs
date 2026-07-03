// -----------------------------------------------------------------------
// <copyright file="SystemdUnitPathDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Validates that the systemd <c>--user</c> unit installed by
/// <c>netclaw daemon install</c> supplies the daemon's shell-tool PATH via a
/// netclaw-owned <c>EnvironmentFile=</c> that resolves the daemon's install
/// directory. Without it, <c>ShellTool</c> and <c>BackgroundJobExecutionActor</c>
/// spawn <c>bash -c</c> with the sanitized systemd default PATH and cannot find
/// <c>netclaw</c>, <c>dotnet</c>, or anything else outside the system path.
/// </summary>
/// <remarks>
/// This is the consumer side of the PATH provisioning contract; the producer is
/// <see cref="DaemonManager"/> install and the rehydrator is <c>DoctorFixService</c>.
/// All three share <see cref="DaemonPathEnvironmentFile"/> so the file format, the
/// <c>EnvironmentFile=</c> wiring, and the install-dir semantics stay in agreement.
///
/// Linux-only. On non-Linux platforms — and on Linux boxes where the operator runs
/// <c>netclaw daemon start</c> directly instead of installing the service — this check
/// passes silently, because the manual daemon inherits the operator's interactive shell
/// PATH and the failure mode does not apply.
/// </remarks>
public sealed class SystemdUnitPathDoctorCheck : IDoctorCheck
{
    private const string CheckName = "Systemd Unit PATH";

    private const string ReinstallRemediation =
        "Reinstall to migrate to the environment-file model: " +
        "`netclaw daemon uninstall && netclaw daemon install`, then `systemctl --user restart netclaw`.";

    private const string RehydrateRemediation =
        "Rehydrate it: `netclaw doctor --fix`, then `systemctl --user restart netclaw`.";

    private readonly string _unitFilePath;
    private readonly bool _enabledOnThisPlatform;

    public SystemdUnitPathDoctorCheck()
        : this(DaemonManager.SystemdUserUnitFilePath, OperatingSystem.IsLinux())
    {
    }

    /// <summary>
    /// Test seam: explicit unit path and platform gate so tests can exercise the
    /// parser on any host without needing a real systemd installation.
    /// </summary>
    internal SystemdUnitPathDoctorCheck(string unitFilePath, bool enabledOnThisPlatform)
    {
        _unitFilePath = unitFilePath;
        _enabledOnThisPlatform = enabledOnThisPlatform;
    }

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabledOnThisPlatform)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "Not applicable on this platform."));

        var unitPath = _unitFilePath;

        if (!File.Exists(unitPath))
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                CheckName,
                "No systemd user service installed (skipping)."));
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(unitPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"Could not read {unitPath}: {ex.Message}",
                "Check file permissions."));
        }

        if (!DaemonPathEnvironmentFile.TryGetInstallDir(lines, out var installDir))
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"Could not determine the daemon install directory from ExecStart in {unitPath}. "
                + "The unit file may be malformed.",
                ReinstallRemediation));
        }

        // Legacy/unwired unit: the pre-#1544 install baked an inline Environment=PATH= and
        // no EnvironmentFile=. Such a unit is still fully functional if its inline PATH
        // resolves the install dir (e.g. after an in-place binary upgrade without reinstall),
        // so pass with a migration nudge rather than a false alarm. Only warn when the inline
        // PATH is missing/incomplete, and route to reinstall (doctor --fix owns only the env
        // file, not unit rewrites).
        if (!DaemonPathEnvironmentFile.TryGetEnvironmentFilePath(lines, out var envFilePath))
        {
            if (DaemonPathEnvironmentFile.TryGetInlinePath(lines, out var inlinePath)
                && DaemonPathEnvironmentFile.PathContainsDirectory(inlinePath, installDir))
            {
                return Task.FromResult(DoctorCheckResult.Pass(
                    CheckName,
                    $"Legacy unit supplies PATH inline and includes {installDir}. Re-run "
                    + "`netclaw daemon install` to migrate to the managed environment file."));
            }

            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"Systemd unit at {unitPath} does not supply the daemon's shell-tool PATH "
                + "(no `EnvironmentFile=`, and no inline PATH that includes the install "
                + "directory). The shell tool will fall back to the sanitized systemd PATH and "
                + "cannot resolve `netclaw`, `dotnet`, or `~/.local/bin` tools.",
                ReinstallRemediation));
        }

        if (!File.Exists(envFilePath))
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"The PATH environment file referenced by {unitPath} is missing ({envFilePath}). "
                + "The daemon's shell tool will fall back to the sanitized systemd PATH.",
                RehydrateRemediation));
        }

        string envContent;
        try
        {
            envContent = File.ReadAllText(envFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"Could not read {envFilePath}: {ex.Message}",
                "Check file permissions."));
        }

        var pathValue = DaemonPathEnvironmentFile.ReadPathValue(envContent);
        if (pathValue is null)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"The PATH environment file {envFilePath} does not set PATH.",
                RehydrateRemediation));
        }

        if (!DaemonPathEnvironmentFile.PathContainsDirectory(pathValue, installDir))
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"The PATH in {envFilePath} does not include the daemon's install directory "
                + $"({installDir}). Shell tool invocations may fail to resolve `netclaw`.",
                RehydrateRemediation));
        }

        return Task.FromResult(DoctorCheckResult.Pass(
            CheckName,
            $"Daemon shell-tool PATH is sourced from {envFilePath} and includes {installDir}."));
    }
}
