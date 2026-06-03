// -----------------------------------------------------------------------
// <copyright file="StatusUpdateChecker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
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
        TimeSpan? timeout = null,
        UpdateChannel channel = UpdateChannel.Stable)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));

        try
        {
            var fetchResult = await UpdateCheckService.FetchVerifiedManifestAsync(httpClient, cts.Token);
            if (!fetchResult.IsSuccess)
                return new StatusUpdateResult("unknown", currentVersion, null, null,
                    $"{fetchResult.Status}: {fetchResult.ErrorMessage}");

            var result = UpdateCheckService.EvaluateManifest(fetchResult.Manifest!, currentVersion, channel);
            return result.IsUpdateAvailable
                ? new StatusUpdateResult("update-available", result.CurrentVersion, result.LatestVersion, result.ReleaseNotesUrl)
                : new StatusUpdateResult("up-to-date", result.CurrentVersion, null, null);
        }
        catch (OperationCanceledException)
        {
            return new StatusUpdateResult("unknown", currentVersion, null, null, "timed out");
        }
        catch (Exception ex)
        {
            return new StatusUpdateResult("unknown", currentVersion, null, null, ex.Message);
        }
    }
}

internal sealed record StatusUpdateResult(
    string State,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseNotesUrl,
    string? ErrorDetail = null);
