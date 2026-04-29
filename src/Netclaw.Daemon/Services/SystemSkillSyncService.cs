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
internal sealed class SystemSkillSyncService : SkillSyncServiceBase, IHostedService
{
    private readonly HttpClient _httpClient;
    private readonly NetclawPaths _paths;
    private readonly SkillSyncConfig _skillSyncConfig;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SystemSkillSyncService> _logger;
    private readonly string _daemonVersion;
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
        : base(scanner, skillRegistry, skillIndexLayer)
    {
        _httpClient = httpClient;
        _paths = paths;
        _skillSyncConfig = skillSyncConfig;
        _timeProvider = timeProvider;
        _logger = logger;
        _daemonVersion = daemonVersion;
        _serverFeedSources = serverFeedSources ?? [];
        _externalSources = externalSources ?? [];
    }

    protected override ILogger Logger => _logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_skillSyncConfig.DisableSystemSkillSync)
        {
            _logger.LogInformation(
                "System skill sync disabled via SkillSync.DisableSystemSkillSync; using on-disk built-in skills only");
            RescanAndUpdateIndex(
                _paths.SkillsDirectory, _serverFeedSources, _externalSources, "sync rebuild");
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

            RescanAndUpdateIndex(
                _paths.SkillsDirectory, _serverFeedSources, _externalSources, "sync rebuild");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "System skill sync failed — continuing with on-disk skills");
            // Still re-scan on-disk skills even if sync failed
            RescanAndUpdateIndex(
                _paths.SkillsDirectory, _serverFeedSources, _externalSources, "sync rebuild");
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

            try
            {
                var resources = entry.Files?.Select(
                    f => new SkillResourceDescriptor(f.Path, f.Url, f.Sha256)).ToList();

                var synced = await SyncSingleSkillAsync(
                    _httpClient,
                    entry.Name,
                    entry.Version,
                    entry.Url,
                    entry.Sha256,
                    resources,
                    _paths.SystemSkillsDirectory,
                    syncState,
                    now,
                    FeedConstants.FeedHttpTimeout,
                    logContext: "",
                    cancellationToken);

                if (synced)
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
