using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Security.Skills;
using Netclaw.SkillClient;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Syncs skills from private skill-server instances at daemon startup using
/// the Cloudflare Agent Skills RFC discovery protocol. Each configured feed
/// is synced independently — one failing server never blocks others.
/// Never blocks startup on network failures; falls back to on-disk skills.
/// </summary>
internal sealed class ServerFeedSkillSyncService : IHostedService
{
    private static readonly string[] AllowedResourcePrefixes = ["references", "scripts", "assets"];

    private readonly SkillFeedsConfig _feedsConfig;
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _skillRegistry;
    private readonly SkillIndexContextLayer _skillIndexLayer;
    private readonly TimeProvider _timeProvider;
    private readonly ISkillContentScanner _scanner;
    private readonly ILogger<ServerFeedSkillSyncService> _logger;
    private readonly IReadOnlyList<ResolvedExternalSource> _serverFeedSources;
    private readonly IReadOnlyList<ResolvedExternalSource> _externalSources;

    public ServerFeedSkillSyncService(
        SkillFeedsConfig feedsConfig,
        NetclawPaths paths,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        TimeProvider timeProvider,
        ISkillContentScanner scanner,
        ILogger<ServerFeedSkillSyncService> logger,
        [Microsoft.Extensions.DependencyInjection.FromKeyedServices("server-feeds")]
        IReadOnlyList<ResolvedExternalSource> serverFeedSources,
        IReadOnlyList<ResolvedExternalSource> externalSources)
    {
        _feedsConfig = feedsConfig;
        _paths = paths;
        _skillRegistry = skillRegistry;
        _skillIndexLayer = skillIndexLayer;
        _timeProvider = timeProvider;
        _scanner = scanner;
        _logger = logger;
        _serverFeedSources = serverFeedSources;
        _externalSources = externalSources;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var anyChanged = false;

        foreach (var feed in _feedsConfig.Feeds.Where(f => f.Enabled))
        {
            try
            {
                var changed = await SyncFeedAsync(feed, cancellationToken);
                anyChanged |= changed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Server feed sync failed for '{FeedName}' ({FeedUrl}) — using on-disk skills",
                    feed.Name, feed.Url);
            }
        }

        RescanAndUpdateIndex();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<bool> SyncFeedAsync(SkillFeedSource feed, CancellationToken cancellationToken)
    {
        var feedDir = _paths.ServerFeedDirectory(feed.Name);
        Directory.CreateDirectory(feedDir);

        var syncState = ReadSyncState(feed.Name);
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
                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    "Server feed '{FeedName}' RFC index fetch failed: {Message} — using on-disk skills",
                    feed.Name, ex.Message);
                return false;
            }
        }

        if (index is null || index.Skills.Count == 0)
        {
            _logger.LogInformation("Server feed '{FeedName}' returned empty index", feed.Name);
            return false;
        }

        _logger.LogInformation(
            "Fetched RFC index from server feed '{FeedName}' ({SkillCount} skills)",
            feed.Name, index.Skills.Count);

        var updated = false;

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
                var mainContent = await DownloadAndVerifyAsync(
                    entry.Url, digestHex, entry.Name, feed, cancellationToken);
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

                var downloadedFiles = new List<DownloadedSkillFile>
                {
                    new("SKILL.md", mainContent)
                };

                if (entry.Resources is { Count: > 0 })
                {
                    var allFilesOk = true;
                    foreach (var resource in entry.Resources)
                    {
                        var normalizedPath = ValidateResourcePath(resource.Path);
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
                            resource.Url, resourceDigest,
                            $"{entry.Name}/{resource.Path}", feed, cancellationToken);
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

                        downloadedFiles.Add(new DownloadedSkillFile(normalizedPath, fileContent));
                    }

                    if (!allFilesOk)
                        continue;
                }

                await ReplaceSkillDirectoryAsync(feed.Name, entry.Name, downloadedFiles, cancellationToken);

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

        if (updated)
        {
            syncState.LastSyncUtc = now;
            WriteSyncState(feed.Name, syncState);
        }

        return updated;
    }

    private async Task<string?> DownloadAndVerifyAsync(
        string url, string expectedSha256Hex, string label,
        SkillFeedSource feed, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(feed.TimeoutSeconds));

            using var httpClient = new HttpClient();
            if (feed.ApiKey is { Value: { } apiKey } && !string.IsNullOrWhiteSpace(apiKey))
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            var content = await httpClient.GetStringAsync(url, cts.Token);

            var hash = ComputeSha256(content);
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

    private SkillSyncState ReadSyncState(string feedName)
    {
        var path = _paths.ServerFeedSyncStatePath(feedName);
        if (!File.Exists(path))
            return new SkillSyncState();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SkillSyncState>(json) ?? new SkillSyncState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read sync state for feed '{FeedName}' — starting fresh", feedName);
            return new SkillSyncState();
        }
    }

    private void WriteSyncState(string feedName, SkillSyncState state)
    {
        var path = _paths.ServerFeedSyncStatePath(feedName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private void RescanAndUpdateIndex()
    {
        // Rebuild resolved server feed sources (directories may have been created during sync)
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

    private async Task ReplaceSkillDirectoryAsync(
        string feedName, string skillName,
        IReadOnlyList<DownloadedSkillFile> files,
        CancellationToken cancellationToken)
    {
        var feedDir = _paths.ServerFeedDirectory(feedName);
        var skillDir = Path.Combine(feedDir, skillName);
        var stagingRoot = Path.Combine(feedDir, ".staging");
        Directory.CreateDirectory(stagingRoot);

        var stagingDir = Path.Combine(stagingRoot, $"{skillName}-{Guid.NewGuid():N}");
        var backupDir = Path.Combine(stagingRoot, $"{skillName}-backup-{Guid.NewGuid():N}");

        Directory.CreateDirectory(stagingDir);

        try
        {
            foreach (var file in files)
            {
                var targetPath = Path.Combine(
                    stagingDir, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await File.WriteAllTextAsync(targetPath, file.Content, cancellationToken);
            }

            if (Directory.Exists(skillDir))
                Directory.Move(skillDir, backupDir);

            Directory.Move(stagingDir, skillDir);

            if (Directory.Exists(backupDir))
                Directory.Delete(backupDir, recursive: true);
        }
        catch
        {
            if (!Directory.Exists(skillDir) && Directory.Exists(backupDir))
                Directory.Move(backupDir, skillDir);

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);

            if (Directory.Exists(backupDir) && !Directory.Exists(skillDir))
                Directory.Delete(backupDir, recursive: true);
        }
    }

    internal static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Strips the <c>sha256:</c> prefix from RFC digest values.
    /// Returns the bare hex string for comparison with computed hashes.
    /// </summary>
    internal static string NormalizeDigest(string digest)
    {
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return digest[7..];
        return digest;
    }

    private static string? ValidateResourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal))
            return null;

        var normalized = path.Replace('\\', '/');
        var firstSegment = normalized.Split('/')[0];
        if (!AllowedResourcePrefixes.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            return null;

        return normalized;
    }

    private sealed record DownloadedSkillFile(string RelativePath, string Content);
}
