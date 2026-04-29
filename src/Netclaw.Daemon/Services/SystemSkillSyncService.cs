// -----------------------------------------------------------------------
// <copyright file="SystemSkillSyncService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Security.Skills;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Syncs system skills from the feed CDN at daemon startup and rebuilds the
/// description menu context layer. The LLM discovers skills by reading the
/// menu and loads them via <c>file_read</c> — no keyword matching needed.
/// Never blocks startup on network — if the feed is unreachable, the daemon
/// starts with whatever skills are already on disk.
/// </summary>
internal sealed class SystemSkillSyncService : IHostedService
{
    private readonly HttpClient _httpClient;
    private readonly NetclawPaths _paths;
    private readonly SkillSyncConfig _skillSyncConfig;
    private readonly SkillRegistry _skillRegistry;
    private readonly SkillIndexContextLayer _skillIndexLayer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SystemSkillSyncService> _logger;
    private readonly string _daemonVersion;
    private readonly ISkillContentScanner _scanner;
    private readonly IReadOnlyList<ResolvedExternalSource> _serverFeedSources;
    private readonly IReadOnlyList<ResolvedExternalSource> _externalSources;

    public SystemSkillSyncService(
        HttpClient httpClient,
        NetclawPaths paths,
        SkillSyncConfig skillSyncConfig,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        TimeProvider timeProvider,
        ISkillContentScanner scanner,
        ILogger<SystemSkillSyncService> logger,
        [Microsoft.Extensions.DependencyInjection.FromKeyedServices("server-feeds")]
        IReadOnlyList<ResolvedExternalSource> serverFeedSources,
        IReadOnlyList<ResolvedExternalSource> externalSources,
        IChatClientProvider? chatClientProvider = null)
        : this(httpClient, paths, skillSyncConfig, skillRegistry, skillIndexLayer,
            timeProvider, scanner, logger, BuildInfo.Version, serverFeedSources, externalSources)
    {
    }

    // Internal constructor for testing — allows injecting a fake daemon version
    internal SystemSkillSyncService(
        HttpClient httpClient,
        NetclawPaths paths,
        SkillSyncConfig skillSyncConfig,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        TimeProvider timeProvider,
        ISkillContentScanner scanner,
        ILogger<SystemSkillSyncService> logger,
        string daemonVersion,
        IReadOnlyList<ResolvedExternalSource>? serverFeedSources = null,
        IReadOnlyList<ResolvedExternalSource>? externalSources = null)
    {
        _httpClient = httpClient;
        _paths = paths;
        _skillSyncConfig = skillSyncConfig;
        _skillRegistry = skillRegistry;
        _skillIndexLayer = skillIndexLayer;
        _timeProvider = timeProvider;
        _scanner = scanner;
        _logger = logger;
        _daemonVersion = daemonVersion;
        _serverFeedSources = serverFeedSources ?? [];
        _externalSources = externalSources ?? [];
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_skillSyncConfig.DisableSystemSkillSync)
        {
            _logger.LogInformation(
                "System skill sync disabled via SkillSync.DisableSystemSkillSync; using on-disk built-in skills only");
            RescanAndUpdateIndex();
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.SystemSkillsDirectory);

            var syncState = ReadSyncState();
            var manifest = await FetchManifestAsync(cancellationToken);

            if (manifest is not null)
            {
                await SyncSkillsAsync(manifest, syncState, cancellationToken);
            }

            RescanAndUpdateIndex();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "System skill sync failed — continuing with on-disk skills");
            // Still re-scan on-disk skills even if sync failed
            RescanAndUpdateIndex();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private SkillSyncState ReadSyncState()
        => SkillSyncHelpers.ReadSyncState(_paths.SkillSyncStatePath, _logger);

    private void WriteSyncState(SkillSyncState state)
        => SkillSyncHelpers.WriteSyncState(_paths.SkillSyncStatePath, state);

    private async Task<SkillFeedManifest?> FetchManifestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(FeedConstants.FeedHttpTimeout);

            var response = await _httpClient.GetAsync(
                FeedConstants.SystemSkillsManifestUrl, cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var manifest = JsonSerializer.Deserialize<SkillFeedManifest>(json);

            if (manifest is null)
            {
                _logger.LogWarning("Feed manifest deserialized to null");
                return null;
            }

            if (manifest.SchemaVersion != 1)
            {
                _logger.LogWarning("Unsupported feed schema version {Version} — skipping sync",
                    manifest.SchemaVersion);
                return null;
            }

            _logger.LogInformation("Fetched skill feed manifest ({SkillCount} skills, updated {UpdatedAt})",
                manifest.Skills.Count, manifest.UpdatedAt);
            return manifest;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Feed manifest fetch timed out — using on-disk skills");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Feed manifest fetch failed: {Message} — using on-disk skills", ex.Message);
            return null;
        }
    }

    private async Task SyncSkillsAsync(
        SkillFeedManifest manifest, SkillSyncState syncState, CancellationToken cancellationToken)
    {
        var updated = false;
        var now = _timeProvider.GetUtcNow();

        foreach (var entry in manifest.Skills)
        {
            // Skip skills that require a newer daemon
            if (!string.IsNullOrEmpty(entry.MinimumDaemonVersion)
                && !IsVersionSatisfied(_daemonVersion, entry.MinimumDaemonVersion))
            {
                _logger.LogDebug(
                    "Skipping skill {SkillName} v{Version} — requires daemon >= {MinVersion} (current: {Current})",
                    entry.Name, entry.Version, entry.MinimumDaemonVersion, _daemonVersion);
                continue;
            }

            // Check if we already have this version
            if (syncState.Skills.TryGetValue(entry.Name, out var existing)
                && existing.Version == entry.Version
                && existing.Sha256 == entry.Sha256)
            {
                continue;
            }

            // Download skill into its directory (skill-name/SKILL.md)
            try
            {
                var mainContent = await DownloadAndVerifyAsync(entry.Url, entry.Sha256, entry.Name, cancellationToken);
                if (mainContent is null)
                    continue;

                var mainScan = await _scanner.ScanAsync(
                    entry.Name,
                    mainContent,
                    cancellationToken);
                if (!mainScan.IsAllowed)
                {
                    _logger.LogWarning(
                        "Rejected synced skill {SkillName} v{Version}: {Reason}",
                        entry.Name,
                        entry.Version,
                        mainScan.Reason);
                    continue;
                }

                var downloadedFiles = new List<DownloadedSkillFile>
                {
                    new("SKILL.md", mainContent)
                };

                if (entry.Files is { Count: > 0 })
                {
                    var allFilesOk = true;
                    foreach (var file in entry.Files)
                    {
                        var normalizedPath = SkillSyncHelpers.ValidateResourcePath(file.Path);
                        if (normalizedPath is null)
                        {
                            _logger.LogWarning(
                                "Rejected synced resource path for {SkillName} v{Version}: {Path}",
                                entry.Name,
                                entry.Version,
                                file.Path);
                            allFilesOk = false;
                            break;
                        }

                        var fileContent = await DownloadAndVerifyAsync(file.Url, file.Sha256, $"{entry.Name}/{file.Path}", cancellationToken);
                        if (fileContent is null)
                        {
                            allFilesOk = false;
                            break;
                        }

                        var fileScan = await _scanner.ScanAsync(
                            $"{entry.Name}:{normalizedPath}",
                            fileContent,
                            cancellationToken);
                        if (!fileScan.IsAllowed)
                        {
                            _logger.LogWarning(
                                "Rejected synced resource for {SkillName} v{Version} at {Path}: {Reason}",
                                entry.Name,
                                entry.Version,
                                normalizedPath,
                                fileScan.Reason);
                            allFilesOk = false;
                            break;
                        }

                        downloadedFiles.Add(new DownloadedSkillFile(normalizedPath, fileContent));
                    }

                    if (!allFilesOk)
                        continue;
                }

                await SkillSyncHelpers.ReplaceSkillDirectoryAsync(
                    _paths.SystemSkillsDirectory, entry.Name, downloadedFiles, cancellationToken);

                syncState.Skills[entry.Name] = new SyncedSkillState
                {
                    Version = entry.Version,
                    Sha256 = entry.Sha256,
                    SyncedAtUtc = now
                };

                _logger.LogInformation("Synced skill {SkillName} v{Version}", entry.Name, entry.Version);
                updated = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to sync skill {SkillName} — keeping existing version", entry.Name);
            }
        }

        // Check for skills removed from manifest — log but don't delete
        foreach (var (name, _) in syncState.Skills)
        {
            if (!manifest.Skills.Exists(s => s.Name == name))
            {
                _logger.LogInformation(
                    "Skill {SkillName} is in sync state but not in manifest — keeping on disk", name);
            }
        }

        if (updated)
        {
            syncState.LastSyncUtc = now;
            WriteSyncState(syncState);
        }
    }

    private async Task<string?> DownloadAndVerifyAsync(
        string url, string expectedSha256, string label, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(FeedConstants.FeedHttpTimeout);

            var content = await _httpClient.GetStringAsync(url, cts.Token);

            var hash = SkillSyncHelpers.ComputeSha256(content);
            if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "SHA-256 mismatch for {Label}: expected {Expected}, got {Actual}",
                    label, expectedSha256, hash);
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

    /// <summary>
    /// Re-scans the entire skills directory and rebuilds the registry + context layer.
    /// Called after sync completes (or fails) to ensure the agent sees current skills.
    /// </summary>
    private void RescanAndUpdateIndex()
    {
        var mergedResult = SkillScanner.ScanAndMerge(
            _paths.SkillsDirectory, _serverFeedSources, _externalSources);
        SkillRegistryUpdater.ApplyMergedScanResult(
            _skillRegistry, _skillIndexLayer, mergedResult, _paths.SkillsDirectory, _externalSources);

        if (mergedResult.Issues.Count > 0)
        {
            _logger.LogWarning(
                "Skill inventory is degraded after sync rebuild: accepted={AcceptedSkillCount} rejected={RejectedIssueCount}",
                mergedResult.AcceptedSkills.Count,
                mergedResult.Issues.Count);

            foreach (var issue in mergedResult.Issues)
            {
                _logger.LogWarning(
                    "Rejected skill item during sync rebuild: kind={IssueKind} path={Path} message={Message}",
                    issue.Kind,
                    issue.Path,
                    issue.Message);
            }
        }
        else
        {
            _logger.LogInformation("Skill description menu updated ({SkillCount} skills)", mergedResult.AcceptedSkills.Count);
        }
    }

    internal static string ComputeSha256(string content)
        => SkillSyncHelpers.ComputeSha256(content);

    internal static bool IsVersionSatisfied(string current, string minimum)
    {
        if (Version.TryParse(current, out var currentVersion)
            && Version.TryParse(minimum, out var minimumVersion))
        {
            return currentVersion >= minimumVersion;
        }

        // If parsing fails, assume satisfied to avoid blocking skills unnecessarily
        return true;
    }
}
