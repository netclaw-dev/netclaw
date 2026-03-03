using System.Runtime.InteropServices;
using System.Text.Json;

namespace Netclaw.Configuration.Feeds;

/// <summary>
/// Result of an update availability check.
/// </summary>
public sealed class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string? ReleaseNotesUrl { get; init; }
    public List<BinaryAsset> MatchingAssets { get; init; } = [];
}

/// <summary>
/// Checks the binary feed manifest for available updates.
/// Shared between CLI (update command + startup check) and daemon (startup notification).
/// Modeled after <see cref="SystemSkillSyncService"/> manifest fetch pattern.
/// </summary>
public static class UpdateCheckService
{
    /// <summary>
    /// Fetches the binary manifest and compares the latest version against the current version.
    /// Never throws — returns a "no update" result on any failure.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        HttpClient httpClient,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var noUpdate = new UpdateCheckResult
        {
            IsUpdateAvailable = false,
            CurrentVersion = currentVersion,
            LatestVersion = currentVersion,
        };

        try
        {
            var manifest = await FetchManifestAsync(httpClient, cancellationToken);
            if (manifest is null)
                return noUpdate;

            return EvaluateManifest(manifest, currentVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return noUpdate;
        }
    }

    /// <summary>
    /// Fetches and deserializes the binary feed manifest.
    /// Returns null on any failure (timeout, HTTP error, deserialization error).
    /// </summary>
    public static async Task<BinaryFeedManifest?> FetchManifestAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FeedConstants.BinaryFeedHttpTimeout);

        var response = await httpClient.GetAsync(
            FeedConstants.BinaryManifestUrl, cts.Token);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cts.Token);
        var manifest = JsonSerializer.Deserialize<BinaryFeedManifest>(json);

        if (manifest is null || manifest.SchemaVersion != 1)
            return null;

        return manifest;
    }

    /// <summary>
    /// Evaluates a manifest against the current version and RID.
    /// Pure function — no I/O.
    /// </summary>
    public static UpdateCheckResult EvaluateManifest(
        BinaryFeedManifest manifest,
        string currentVersion)
    {
        var rid = GetCurrentRid();

        if (string.IsNullOrEmpty(manifest.Latest))
        {
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                CurrentVersion = currentVersion,
                LatestVersion = currentVersion,
            };
        }

        var isNewer = IsNewerVersion(currentVersion, manifest.Latest);

        // Find the latest release entry
        var latestRelease = manifest.Releases
            .FirstOrDefault(r => r.Version == manifest.Latest);

        var matchingAssets = latestRelease?.Assets
            .Where(a => string.Equals(a.Rid, rid, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        return new UpdateCheckResult
        {
            IsUpdateAvailable = isNewer && matchingAssets.Count > 0,
            CurrentVersion = currentVersion,
            LatestVersion = manifest.Latest,
            ReleaseNotesUrl = latestRelease?.ReleaseNotesUrl,
            MatchingAssets = matchingAssets,
        };
    }

    /// <summary>
    /// Returns the .NET runtime identifier for the current platform.
    /// </summary>
    public static string GetCurrentRid()
    {
        // RuntimeInformation.RuntimeIdentifier gives the actual RID at runtime
        // (e.g. "linux-x64", "linux-arm64", "win-x64")
        return RuntimeInformation.RuntimeIdentifier;
    }

    /// <summary>
    /// Returns true if <paramref name="latest"/> is newer than <paramref name="current"/>.
    /// </summary>
    public static bool IsNewerVersion(string current, string latest)
    {
        if (Version.TryParse(current, out var currentVersion)
            && Version.TryParse(latest, out var latestVersion))
        {
            return latestVersion > currentVersion;
        }

        // If parsing fails, treat as no update to avoid false positives
        return false;
    }
}
