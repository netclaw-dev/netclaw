// -----------------------------------------------------------------------
// <copyright file="DaemonEnvironmentFileModeDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Reports a <c>daemon.env</c> that group or others can read. The file holds plain-text values that
/// the operator wrote, so install, uninstall, and <c>doctor --fix</c> keep it at mode 0600. An older
/// install or a hand edit can leave it open. <c>doctor --fix</c> sets the mode and changes nothing else.
/// A link that points to a missing file, or that loops, gets a warning and no fix.
/// </summary>
public sealed class DaemonEnvironmentFileModeDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "Daemon Env File Mode";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var envPath = paths.DaemonEnvironmentFilePath;
        if (DaemonPathEnvironmentFile.FindLinkProblem(envPath) is { } linkProblem)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                linkProblem,
                "`netclaw doctor --fix` does not change a broken link. Replace the link with a regular file, or remove it and run `netclaw daemon install`."));
        }

        if (!DaemonPathEnvironmentFile.HasLooseMode(envPath))
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, $"{envPath} is not open to group or others."));

        return Task.FromResult(DoctorCheckResult.Warning(
            CheckName,
            $"{envPath} can be read by other users, and it can hold plain-text values.",
            "Run `netclaw doctor --fix` to set the mode to 0600."));
    }
}
