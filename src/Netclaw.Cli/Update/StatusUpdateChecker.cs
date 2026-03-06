using Netclaw.Configuration.Feeds;

namespace Netclaw.Cli.Update;

/// <summary>
/// Performs an update availability check for the <c>netclaw status</c> command.
/// Unlike <see cref="UpdateCheckService.CheckForUpdateAsync"/>, this distinguishes
/// "up-to-date" from "unknown" (timeout or network failure).
/// </summary>
internal static class StatusUpdateChecker
{
    /// <summary>
    /// Checks for updates with a 3-second timeout (configurable for testing).
    /// Returns ("update-available"|"up-to-date"|"unknown", currentVersion, latestVersion, releaseNotesUrl).
    /// Never throws.
    /// </summary>
    internal static async Task<StatusUpdateResult> CheckAsync(
        HttpClient httpClient,
        string currentVersion,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));

        try
        {
            var manifest = await UpdateCheckService.FetchManifestAsync(httpClient, cts.Token);
            if (manifest is null)
                return new StatusUpdateResult("unknown", currentVersion, null, null);

            var result = UpdateCheckService.EvaluateManifest(manifest, currentVersion);
            return result.IsUpdateAvailable
                ? new StatusUpdateResult("update-available", result.CurrentVersion, result.LatestVersion, result.ReleaseNotesUrl)
                : new StatusUpdateResult("up-to-date", result.CurrentVersion, null, null);
        }
        catch (Exception)
        {
            // OperationCanceledException (3s timeout) or HttpRequestException (network failure)
            return new StatusUpdateResult("unknown", currentVersion, null, null);
        }
    }
}

internal sealed record StatusUpdateResult(
    string State,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseNotesUrl);
