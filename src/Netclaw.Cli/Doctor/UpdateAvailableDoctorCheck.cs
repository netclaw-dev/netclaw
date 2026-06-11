// -----------------------------------------------------------------------
// <copyright file="UpdateAvailableDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Checks if a newer version of Netclaw is available on the configured update channel.
/// Uses a short timeout to avoid slowing down doctor runs.
/// </summary>
public sealed class UpdateAvailableDoctorCheck : IDoctorCheck
{
    private const string CheckName = "Update";

    private readonly UpdateChannel _channel;

    // Take the channel from the bound DaemonConfig (the same value every other update
    // surface uses) rather than re-reading netclaw.json — no bespoke config parsing, and
    // a malformed value is reported by ConfigSchemaDoctorCheck instead of crashing here.
    public UpdateAvailableDoctorCheck(DaemonConfig daemonConfig) => _channel = daemonConfig.UpdateChannel;

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var httpClient = new HttpClient();
            // FullVersion so a beta build reports its prerelease suffix; the configured
            // channel keeps doctor consistent with the daemon/CLI (a beta user is told
            // about the next beta, a stable user only about stable releases).
            var result = await UpdateCheckService.CheckForUpdateAsync(
                httpClient, BuildInfo.FullVersion, cts.Token, _channel);

            if (result.IsUpdateAvailable)
            {
                return DoctorCheckResult.Warning(
                    CheckName,
                    $"Update available: v{result.CurrentVersion} → v{result.LatestVersion}",
                    "Run `netclaw update` to upgrade.");
            }

            return DoctorCheckResult.Pass(CheckName,
                $"Up to date (v{result.CurrentVersion}).");
        }
        catch
        {
            return DoctorCheckResult.Pass(CheckName,
                $"Could not check for updates (v{BuildInfo.FullVersion}).");
        }
    }
}
