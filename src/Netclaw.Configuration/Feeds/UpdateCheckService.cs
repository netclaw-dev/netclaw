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
    private static UpdateCheckResult? s_cachedResult;
    private static DateTimeOffset s_cachedAt;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// Returns the most recent cached result, or null if no check has been performed yet.
    /// </summary>
    public static UpdateCheckResult? GetLastResult() => s_cachedResult;

    /// <summary>
    /// Fetches the binary manifest and compares the latest version against the current version.
    /// Returns a cached result if one exists and is less than 1 hour old.
    /// Never throws — returns a "no update" result on any failure.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        HttpClient httpClient,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        // Return cached result if fresh
        var cached = s_cachedResult;
        if (cached is not null && DateTimeOffset.UtcNow - s_cachedAt < CacheDuration)
            return cached;

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
                CacheResult(failed);
                return failed;
            }

            var result = EvaluateManifest(fetchResult.Manifest!, currentVersion);
            CacheResult(result);
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
            CacheResult(failed);
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

    private static void CacheResult(UpdateCheckResult result)
    {
        s_cachedResult = result;
        s_cachedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Clears the cached result. Used by tests to avoid cross-test interference.
    /// </summary>
    public static void ResetCache()
    {
        s_cachedResult = null;
        s_cachedAt = default;
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
