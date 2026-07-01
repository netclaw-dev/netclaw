// -----------------------------------------------------------------------
// <copyright file="ServerFeedSkillSyncService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Security.Skills;
using Netclaw.SkillClient;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Syncs skills from private skill-server instances at daemon startup and
/// periodically thereafter using the Cloudflare Agent Skills RFC discovery
/// protocol. Each configured feed is synced independently — one failing
/// server never blocks others. Never blocks startup on network failures;
/// falls back to on-disk skills.
/// </summary>
internal sealed class ServerFeedSkillSyncService : BackgroundService
{
    private readonly SkillFeedsConfig _feedsConfig;
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _skillRegistry;
    private readonly SkillIndexContextLayer _skillIndexLayer;
    private readonly TimeProvider _timeProvider;
    private readonly ISkillContentScanner _scanner;
    private readonly ILogger<ServerFeedSkillSyncService> _logger;
    private readonly IReadOnlyList<ResolvedExternalSource> _externalSources;

    // Random jitter (0–5 min) so multiple daemon instances don't all poll at once
    private readonly TimeSpan _initialJitter;

    public ServerFeedSkillSyncService(
        SkillFeedsConfig feedsConfig,
        NetclawPaths paths,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        TimeProvider timeProvider,
        ISkillContentScanner scanner,
        ILogger<ServerFeedSkillSyncService> logger,
        IReadOnlyList<ResolvedExternalSource> externalSources)
    {
        _feedsConfig = feedsConfig;
        _paths = paths;
        _skillRegistry = skillRegistry;
        _skillIndexLayer = skillIndexLayer;
        _timeProvider = timeProvider;
        _scanner = scanner;
        _logger = logger;
        _externalSources = externalSources;
        _initialJitter = TimeSpan.FromSeconds(Random.Shared.Next(0, 300));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial sync at startup — no jitter
        await SyncAllFeedsAsync(stoppingToken);

        var intervalMinutes = _feedsConfig.SyncIntervalMinutes;
        if (intervalMinutes <= 0)
        {
            _logger.LogInformation("Periodic server feed sync disabled (SyncIntervalMinutes=0)");
            return;
        }

        var interval = TimeSpan.FromMinutes(intervalMinutes);

        // First periodic tick includes jitter to stagger across instances
        var firstDelay = interval + _initialJitter;
        _logger.LogInformation(
            "Server feed periodic sync scheduled every {IntervalMinutes}m (first check in {FirstDelayMinutes:F1}m)",
            intervalMinutes, firstDelay.TotalMinutes);

        using var timer = new PeriodicTimer(interval, _timeProvider);

        // Wait the jittered first interval
        try
        {
            await Task.Delay(firstDelay, _timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // First periodic sync
        await SyncAllFeedsAsync(stoppingToken);

        // Subsequent syncs on the regular interval
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncAllFeedsAsync(stoppingToken);
        }
    }

    private async Task SyncAllFeedsAsync(CancellationToken cancellationToken)
    {
        foreach (var feed in _feedsConfig.Feeds.Where(f => f.Enabled))
        {
            try
            {
                await SyncFeedAsync(feed, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Server feed sync failed for '{FeedName}' ({FeedUrl}) — using on-disk skills",
                    feed.Name, feed.Url);
            }
        }

        // Rebuild the registry from disk. Guarded because the scan walks the same
        // feed tree this service (and others) may be mutating concurrently — a
        // directory vanishing mid-scan must not tear down the background service.
        try
        {
            RescanAndUpdateIndex();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Skill index rebuild after server feed sync failed — registry left as-is");
        }
    }

    private async Task SyncFeedAsync(SkillFeedSource feed, CancellationToken cancellationToken)
    {
        var feedDir = _paths.ServerFeedDirectory(feed.Name);
        Directory.CreateDirectory(feedDir);

        var syncState = SkillSyncHelpers.ReadSyncState(
            _paths.ServerFeedSyncStatePath(feed.Name), _logger);
        var now = _timeProvider.GetUtcNow();

        RfcSkillIndex? index;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(feed.TimeoutSeconds));
            try
            {
                using var client = new SkillServerClient(feed.Url, feed.ApiKey?.Value);
                index = await client.GetRfcIndexAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Server feed '{FeedName}' RFC index fetch timed out — using on-disk skills",
                    feed.Name);
                return;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    "Server feed '{FeedName}' RFC index fetch failed: {Message} — using on-disk skills",
                    feed.Name, ex.Message);
                return;
            }
        }

        if (index is null || index.Skills.Count == 0)
        {
            _logger.LogDebug("Server feed '{FeedName}' returned empty index", feed.Name);
            return;
        }

        _logger.LogDebug(
            "Fetched RFC index from server feed '{FeedName}' ({SkillCount} skills)",
            feed.Name, index.Skills.Count);

        var updated = false;

        using var httpClient = CreateHttpClientForFeed(feed);

        foreach (var entry in index.Skills)
        {
            var digestHex = NormalizeDigest(entry.Digest);
            var version = entry.Version ?? "unknown";

            if (syncState.Skills.TryGetValue(entry.Name, out var existing)
                && existing.Version == version
                && string.Equals(existing.Sha256, digestHex, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                IReadOnlyList<DownloadedSkillFile> downloadedFiles;
                if (IsArchiveEntry(entry))
                {
                    var archiveBytes = await DownloadBytesAndVerifyAsync(
                        httpClient, entry.Url, digestHex, entry.Name, feed.TimeoutSeconds, cancellationToken);
                    if (archiveBytes is null)
                        continue;

                    var archiveFiles = await ExtractArchiveFilesAsync(
                        archiveBytes, entry.Name, feed.Name, _scanner, _logger, cancellationToken);
                    if (archiveFiles is null)
                        continue;

                    downloadedFiles = archiveFiles;
                }
                else
                {
                    var mainContent = await DownloadAndVerifyAsync(
                        httpClient, entry.Url, digestHex, entry.Name, feed.TimeoutSeconds, cancellationToken);
                    if (mainContent is null)
                        continue;

                    var mainScan = await _scanner.ScanAsync(entry.Name, mainContent, cancellationToken);
                    if (!mainScan.IsAllowed)
                    {
                        _logger.LogWarning(
                            "Rejected skill '{SkillName}' from feed '{FeedName}': {Reason}",
                            entry.Name, feed.Name, mainScan.Reason);
                        continue;
                    }

                    var downloadedFileList = new List<DownloadedSkillFile>
                    {
                        DownloadedSkillFile.FromText("SKILL.md", mainContent)
                    };

                    if (entry.Resources is { Count: > 0 })
                    {
                        var allFilesOk = true;
                        foreach (var resource in entry.Resources)
                        {
                            var normalizedPath = SkillSyncHelpers.ValidateResourcePath(resource.Path);
                            if (normalizedPath is null)
                            {
                                _logger.LogWarning(
                                    "Rejected resource path for '{SkillName}' from feed '{FeedName}': {Path}",
                                    entry.Name, feed.Name, resource.Path);
                                allFilesOk = false;
                                break;
                            }

                            var resourceDigest = NormalizeDigest(resource.Digest);
                            var fileContent = await DownloadAndVerifyAsync(
                                httpClient, resource.Url, resourceDigest,
                                $"{entry.Name}/{resource.Path}", feed.TimeoutSeconds, cancellationToken);
                            if (fileContent is null)
                            {
                                allFilesOk = false;
                                break;
                            }

                            var fileScan = await _scanner.ScanAsync(
                                $"{entry.Name}:{normalizedPath}", fileContent, cancellationToken);
                            if (!fileScan.IsAllowed)
                            {
                                _logger.LogWarning(
                                    "Rejected resource for '{SkillName}' from feed '{FeedName}' at {Path}: {Reason}",
                                    entry.Name, feed.Name, normalizedPath, fileScan.Reason);
                                allFilesOk = false;
                                break;
                            }

                            downloadedFileList.Add(DownloadedSkillFile.FromText(normalizedPath, fileContent));
                        }

                        if (!allFilesOk)
                            continue;
                    }

                    downloadedFiles = downloadedFileList;
                }

                await SkillSyncHelpers.ReplaceSkillDirectoryAsync(
                    feedDir, entry.Name, downloadedFiles, cancellationToken);

                syncState.Skills[entry.Name] = new SyncedSkillState
                {
                    Version = version,
                    Sha256 = digestHex,
                    SyncedAtUtc = now
                };

                _logger.LogInformation(
                    "Synced skill '{SkillName}' v{Version} from feed '{FeedName}'",
                    entry.Name, version, feed.Name);
                updated = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to sync skill '{SkillName}' from feed '{FeedName}' — keeping existing version",
                    entry.Name, feed.Name);
            }
        }

        // Reverse pass: drop skills the server no longer advertises. This is
        // only reached with a confirmed, non-empty index (see the early returns
        // above), so a transient outage or empty response never triggers a prune.
        var serverSkillNames = index.Skills.Select(e => e.Name).ToList();
        if (SkillSyncHelpers.PruneRemovedSkills(feedDir, serverSkillNames, syncState, _logger))
            updated = true;

        if (updated)
        {
            syncState.LastSyncUtc = now;
            SkillSyncHelpers.WriteSyncState(
                _paths.ServerFeedSyncStatePath(feed.Name), syncState);
        }
    }

    private static HttpClient CreateHttpClientForFeed(SkillFeedSource feed)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", NetclawUserAgent.Value);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            NetclawUserAgent.ComponentHeader, "skill-feed");
        if (feed.ApiKey is { Value: { } apiKey } && !string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }
        return client;
    }

    private async Task<string?> DownloadAndVerifyAsync(
        HttpClient httpClient, string url, string expectedSha256Hex, string label,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var content = await httpClient.GetStringAsync(url, cts.Token);

            var hash = SkillSyncHelpers.ComputeSha256(content);
            if (!string.Equals(hash, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "SHA-256 mismatch for {Label}: expected {Expected}, got {Actual}",
                    label, expectedSha256Hex, hash);
                return null;
            }

            return content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Download timed out for {Label}", label);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Download failed for {Label}: {Message}", label, ex.Message);
            return null;
        }
    }

    private async Task<byte[]?> DownloadBytesAndVerifyAsync(
        HttpClient httpClient, string url, string expectedSha256Hex, string label,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var content = await httpClient.GetByteArrayAsync(url, cts.Token);

            var hash = SkillSyncHelpers.ComputeSha256(content);
            if (!string.Equals(hash, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "SHA-256 mismatch for {Label}: expected {Expected}, got {Actual}",
                    label, expectedSha256Hex, hash);
                return null;
            }

            return content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Download timed out for {Label}", label);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Download failed for {Label}: {Message}", label, ex.Message);
            return null;
        }
    }

    internal static async Task<IReadOnlyList<DownloadedSkillFile>?> ExtractArchiveFilesAsync(
        byte[] archiveBytes,
        string skillName,
        string feedName,
        ISkillContentScanner scanner,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            var files = new List<DownloadedSkillFile>();
            var seen = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalizedPath = NormalizeArchiveEntryPath(entry.FullName);
                if (normalizedPath is null)
                {
                    logger.LogWarning(
                        "Rejected archive entry for '{SkillName}' from feed '{FeedName}': {Path}",
                        skillName, feedName, entry.FullName);
                    return null;
                }

                if (!seen.Add(normalizedPath))
                {
                    logger.LogWarning(
                        "Rejected archive for '{SkillName}' from feed '{FeedName}': duplicate path {Path}",
                        skillName, feedName, normalizedPath);
                    return null;
                }

                await using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                await entryStream.CopyToAsync(buffer, cancellationToken);
                var bytes = buffer.ToArray();

                string text;
                try
                {
                    text = SkillSyncHelpers.StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    logger.LogWarning(
                        "Rejected archive entry for '{SkillName}' from feed '{FeedName}' at {Path}: non-UTF8 resource content",
                        skillName, feedName, normalizedPath);
                    return null;
                }

                var scanName = string.Equals(normalizedPath, "SKILL.md", StringComparison.Ordinal)
                    ? skillName
                    : $"{skillName}:{normalizedPath}";
                var scanResult = await scanner.ScanAsync(scanName, text, cancellationToken);
                if (!scanResult.IsAllowed)
                {
                    logger.LogWarning(
                        "Rejected archive entry for '{SkillName}' from feed '{FeedName}' at {Path}: {Reason}",
                        skillName, feedName, normalizedPath, scanResult.Reason);
                    return null;
                }

                files.Add(new DownloadedSkillFile(normalizedPath, bytes));
            }

            if (!seen.Contains("SKILL.md"))
            {
                logger.LogWarning(
                    "Rejected archive for '{SkillName}' from feed '{FeedName}': missing SKILL.md",
                    skillName, feedName);
                return null;
            }

            return files;
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex,
                "Rejected archive for '{SkillName}' from feed '{FeedName}': invalid zip archive",
                skillName, feedName);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex,
                "Rejected archive for '{SkillName}' from feed '{FeedName}': unable to read zip archive",
                skillName, feedName);
            return null;
        }
    }

    private static string? NormalizeArchiveEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = path.Replace('\\', '/');
        if (normalized.EndsWith("/", StringComparison.Ordinal))
            return null;

        if (string.Equals(normalized, "SKILL.md", StringComparison.Ordinal))
            return normalized;

        return SkillSyncHelpers.ValidateResourcePath(normalized);
    }

    private static bool IsArchiveEntry(RfcSkillEntry entry)
        => string.Equals(entry.Type, "archive", StringComparison.OrdinalIgnoreCase);

    private void RescanAndUpdateIndex()
    {
        var resolvedServerFeeds = new List<ResolvedExternalSource>();
        foreach (var feed in _feedsConfig.Feeds.Where(f => f.Enabled))
        {
            var feedDir = _paths.ServerFeedDirectory(feed.Name);
            if (Directory.Exists(feedDir))
                resolvedServerFeeds.Add(new ResolvedExternalSource(
                    $"server-feed:{feed.Name}", [feedDir], AllowSymlinks: false));
        }

        var mergedResult = SkillScanner.ScanAndMerge(
            _paths.SkillsDirectory, resolvedServerFeeds, _externalSources);
        SkillRegistryUpdater.ApplyMergedScanResult(
            _skillRegistry, _skillIndexLayer, mergedResult,
            _paths.SkillsDirectory, _externalSources);

        if (mergedResult.Issues.Count > 0)
        {
            _logger.LogWarning(
                "Skill inventory is degraded after server feed sync: accepted={AcceptedSkillCount} rejected={RejectedIssueCount}",
                mergedResult.AcceptedSkills.Count, mergedResult.Issues.Count);

            foreach (var issue in mergedResult.Issues)
            {
                _logger.LogWarning(
                    "Rejected skill item during server feed sync rebuild: kind={IssueKind} path={Path} message={Message}",
                    issue.Kind, issue.Path, issue.Message);
            }
        }
        else
        {
            _logger.LogInformation(
                "Skill index updated after server feed sync ({SkillCount} skills)",
                mergedResult.AcceptedSkills.Count);
        }
    }

    internal static string NormalizeDigest(string digest)
    {
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return digest[7..];
        return digest;
    }
}
