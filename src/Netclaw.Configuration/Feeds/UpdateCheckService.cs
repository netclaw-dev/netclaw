// -----------------------------------------------------------------------
// <copyright file="UpdateCheckService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.InteropServices;
using System.Text.Json;
using Netclaw.Configuration.Security;

namespace Netclaw.Configuration.Feeds;

/// <summary>
/// Result of an update availability check.
/// </summary>
public sealed class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; init; }

    /// <summary>
    /// Whether the check completed successfully (manifest fetched and verified).
    /// When false, <see cref="IsUpdateAvailable"/> is meaningless — the check failed.
    /// </summary>
    public bool CheckSucceeded { get; init; } = true;

    /// <summary>
    /// Human-readable error detail when <see cref="CheckSucceeded"/> is false.
    /// </summary>
    public string? ErrorDetail { get; init; }

    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string? ReleaseNotesUrl { get; init; }
    public List<BinaryAsset> MatchingAssets { get; init; } = [];
}

/// <summary>
/// Result of fetching the binary feed manifest, distinguishing success from
/// different failure modes so callers can display appropriate messages.
/// </summary>
public sealed class ManifestFetchResult
{
    public BinaryFeedManifest? Manifest { get; init; }
    public ManifestFetchStatus Status { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsSuccess => Status == ManifestFetchStatus.Success && Manifest is not null;
}

public enum ManifestFetchStatus
{
    /// <summary>Manifest fetched, signature verified, deserialized successfully.</summary>
    Success,

    /// <summary>Network error, timeout, or HTTP error fetching manifest or signature.</summary>
    NetworkFailure,

    /// <summary>Manifest signature verification failed — possible tampering.</summary>
    SignatureFailure,

    /// <summary>Cryptographic signature verification is unavailable on this platform.</summary>
    PlatformUnavailable,
}

/// <summary>
/// Checks the binary feed manifest for available updates.
/// Shared between CLI (update command + startup check) and daemon (startup notification).
/// Modeled after <see cref="SystemSkillSyncService"/> manifest fetch pattern.
/// Results are cached for 1 hour to avoid hammering the CDN.
/// </summary>
public static class UpdateCheckService
{
    // Last computed result, kept so the daemon /status API can report update availability
    // via GetLastResult() without its own network fetch. This is a last-result store, NOT
    // a TTL cache — every CheckForUpdateAsync call recomputes (a cache keyed only on
    // freshness would silently return another channel's result; the daemon already
    // throttles network calls via its 24h recheck timer).
    private static UpdateCheckResult? s_lastResult;

    /// <summary>
    /// Returns the most recent result, or null if no check has been performed yet.
    /// </summary>
    public static UpdateCheckResult? GetLastResult() => s_lastResult;

    /// <summary>
    /// Fetches the binary manifest and compares the latest version for <paramref name="channel"/>
    /// against <paramref name="currentVersion"/>. Records the result for <see cref="GetLastResult"/>.
    /// Never throws — returns a "no update" result on any failure.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        HttpClient httpClient,
        string currentVersion,
        CancellationToken cancellationToken,
        UpdateChannel channel)
    {
        try
        {
            var fetchResult = await FetchVerifiedManifestAsync(httpClient, cancellationToken);
            if (!fetchResult.IsSuccess)
            {
                var failed = new UpdateCheckResult
                {
                    IsUpdateAvailable = false,
                    CheckSucceeded = false,
                    ErrorDetail = $"{fetchResult.Status}: {fetchResult.ErrorMessage}",
                    CurrentVersion = currentVersion,
                    LatestVersion = currentVersion,
                };
                RecordResult(failed);
                return failed;
            }

            var result = EvaluateManifest(fetchResult.Manifest!, currentVersion, channel);
            RecordResult(result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                CheckSucceeded = false,
                ErrorDetail = ex.Message,
                CurrentVersion = currentVersion,
                LatestVersion = currentVersion,
            };
            RecordResult(failed);
            return failed;
        }
    }

    /// <summary>
    /// Fetches the binary manifest with signature verification and returns a detailed result.
    /// Used by <see cref="Netclaw.Cli.Update.UpdateCommand"/> to display appropriate error messages.
    /// </summary>
    public static async Task<ManifestFetchResult> FetchVerifiedManifestAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FeedConstants.BinaryFeedHttpTimeout);

        // Start both requests in parallel — halves network time for the CLI's 3s timeout
        var sigTask = httpClient.GetAsync(FeedConstants.BinaryManifestSignatureUrl, cts.Token);

        // Read raw bytes for signature verification — avoids encoding round-trip
        // (ReadAsStringAsync → UTF8.GetBytes can alter bytes if the response charset
        // differs from UTF-8, breaking the Ed25519 signature).
        byte[] manifestBytes;
        try
        {
            var response = await httpClient.GetAsync(
                FeedConstants.BinaryManifestUrl, cts.Token);
            response.EnsureSuccessStatusCode();
            manifestBytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ManifestFetchResult
            {
                Status = ManifestFetchStatus.NetworkFailure,
                ErrorMessage = $"Failed to fetch manifest: {ex.Message}",
            };
        }

        // Await the already-in-flight signature request
        string signatureContent;
        try
        {
            var sigResponse = await sigTask;
            sigResponse.EnsureSuccessStatusCode();
            signatureContent = await sigResponse.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ManifestFetchResult
            {
                Status = ManifestFetchStatus.SignatureFailure,
                ErrorMessage = $"Failed to fetch manifest signature: {ex.Message}",
            };
        }

        var verifyResult = MinisignVerifier.Verify(manifestBytes, signatureContent);

        if (verifyResult == MinisignVerifier.VerifyResult.PlatformUnavailable)
        {
            return new ManifestFetchResult
            {
                Status = ManifestFetchStatus.PlatformUnavailable,
                ErrorMessage = "Cryptographic library unavailable on this platform — signature verification could not run",
            };
        }

        if (verifyResult != MinisignVerifier.VerifyResult.Valid)
        {
            return new ManifestFetchResult
            {
                Status = ManifestFetchStatus.SignatureFailure,
                ErrorMessage = verifyResult switch
                {
                    MinisignVerifier.VerifyResult.MalformedSignature =>
                        "Manifest signature file is malformed",
                    MinisignVerifier.VerifyResult.UnsupportedAlgorithm =>
                        "Manifest signature uses an unsupported algorithm",
                    MinisignVerifier.VerifyResult.KeyMismatch =>
                        "Manifest was signed by an unrecognized key",
                    MinisignVerifier.VerifyResult.InvalidSignature =>
                        "Manifest signature is invalid — the manifest may have been tampered with",
                    _ => "Manifest signature verification failed",
                },
            };
        }

        // Signature verified — safe to deserialize
        var manifest = JsonSerializer.Deserialize<BinaryFeedManifest>(manifestBytes);
        if (manifest is null || manifest.SchemaVersion != 1)
        {
            return new ManifestFetchResult
            {
                Status = ManifestFetchStatus.NetworkFailure,
                ErrorMessage = "Manifest deserialization failed or unsupported schema version",
            };
        }

        return new ManifestFetchResult
        {
            Manifest = manifest,
            Status = ManifestFetchStatus.Success,
        };
    }

    private static void RecordResult(UpdateCheckResult result) => s_lastResult = result;

    /// <summary>
    /// Clears the recorded result. Used by tests to avoid cross-test interference.
    /// </summary>
    public static void ResetCache() => s_lastResult = null;

    /// <summary>
    /// Evaluates a manifest against the current version, RID, and release channel.
    /// Pure function — no I/O.
    /// </summary>
    /// <remarks>
    /// The <paramref name="channel"/> decides which version pointer is compared:
    /// <see cref="UpdateChannel.Stable"/> only ever reads <see cref="BinaryFeedManifest.Latest"/>,
    /// so a stable client can never be offered a prerelease. <see cref="UpdateChannel.Beta"/>
    /// reads <see cref="BinaryFeedManifest.LatestPrerelease"/> (the newest of {stable, prerelease}),
    /// falling back to <c>Latest</c> only on manifests published before the prerelease channel existed.
    /// </remarks>
    public static UpdateCheckResult EvaluateManifest(
        BinaryFeedManifest manifest,
        string currentVersion,
        UpdateChannel channel)
    {
        var rid = GetCurrentRid();

        // Resolve the target pointer for the channel. Beta uses latestPrerelease when
        // present (always >= latest); an older manifest without it falls back to latest.
        var targetVersion = channel == UpdateChannel.Beta && !string.IsNullOrEmpty(manifest.LatestPrerelease)
            ? manifest.LatestPrerelease
            : manifest.Latest;

        if (string.IsNullOrEmpty(targetVersion))
        {
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                CurrentVersion = currentVersion,
                LatestVersion = currentVersion,
            };
        }

        var isNewer = IsNewerVersion(currentVersion, targetVersion);

        // Find the release entry for the resolved target version.
        var targetRelease = manifest.Releases
            .FirstOrDefault(r => r.Version == targetVersion);

        var matchingAssets = targetRelease?.Assets
            .Where(a => string.Equals(a.Rid, rid, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        return new UpdateCheckResult
        {
            IsUpdateAvailable = isNewer && matchingAssets.Count > 0,
            CurrentVersion = currentVersion,
            LatestVersion = targetVersion,
            ReleaseNotesUrl = targetRelease?.ReleaseNotesUrl,
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
    /// Uses SemVer 2.0.0 precedence (see <see cref="SemVer"/>), so prerelease versions
    /// like "0.19.0-beta.1" compare correctly — unlike <see cref="System.Version"/>, which
    /// cannot parse a prerelease suffix at all. On a parse failure this returns false
    /// (fail safe: never offer an update we can't reason about).
    /// </summary>
    public static bool IsNewerVersion(string current, string latest)
        => SemVer.IsNewer(current, latest);
}
