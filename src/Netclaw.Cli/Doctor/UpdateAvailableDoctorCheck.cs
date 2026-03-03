using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Checks if a newer version of Netclaw is available.
/// Uses a short timeout to avoid slowing down doctor runs.
/// </summary>
public sealed class UpdateAvailableDoctorCheck : IDoctorCheck
{
    private const string CheckName = "Update";

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var httpClient = new HttpClient();
            var result = await UpdateCheckService.CheckForUpdateAsync(
                httpClient, BuildInfo.Version, cts.Token);

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
                $"Could not check for updates (v{BuildInfo.Version}).");
        }
    }
}
