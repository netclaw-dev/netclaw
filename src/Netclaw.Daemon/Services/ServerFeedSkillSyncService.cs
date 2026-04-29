// -----------------------------------------------------------------------
// <copyright file="ServerFeedSkillSyncService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ServerFeedSkillSyncService> _logger;
    private readonly IReadOnlyList<ResolvedExternalSource> _externalSources;
    private readonly ServerFeedSyncHelper _syncHelper;

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
        _timeProvider = timeProvider;
        _logger = logger;
        _externalSources = externalSources;
        _initialJitter = TimeSpan.FromSeconds(Random.Shared.Next(0, 300));
        _syncHelper = new ServerFeedSyncHelper(scanner, skillRegistry, skillIndexLayer, logger);
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
                await _syncHelper.SyncFeedAsync(feed, _paths, _timeProvider, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Server feed sync failed for '{FeedName}' ({FeedUrl}) — using on-disk skills",
                    feed.Name, feed.Url);
            }
        }

        var resolvedServerFeeds = new List<ResolvedExternalSource>();
        foreach (var feed in _feedsConfig.Feeds.Where(f => f.Enabled))
        {
            var feedDir = _paths.ServerFeedDirectory(feed.Name);
            if (Directory.Exists(feedDir))
                resolvedServerFeeds.Add(new ResolvedExternalSource(
                    $"server-feed:{feed.Name}", [feedDir], AllowSymlinks: false));
        }

        _syncHelper.RescanAndUpdateIndex(
            _paths.SkillsDirectory, resolvedServerFeeds, _externalSources, "server feed sync");
    }

    internal static string NormalizeDigest(string digest)
    {
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return digest[7..];
        return digest;
    }

    /// <summary>
    /// Inner helper that extends <see cref="SkillSyncServiceBase"/> to reuse
    /// download-and-verify and per-skill sync logic. Separated from the
    /// <see cref="BackgroundService"/> hosting so the base class stays
    /// hosting-agnostic.
    /// </summary>
    private sealed class ServerFeedSyncHelper : SkillSyncServiceBase
    {
        private readonly ILogger _logger;

        internal ServerFeedSyncHelper(
            ISkillContentScanner scanner,
            SkillRegistry skillRegistry,
            SkillIndexContextLayer skillIndexLayer,
            ILogger logger)
            : base(scanner, skillRegistry, skillIndexLayer)
        {
            _logger = logger;
        }

        protected override ILogger Logger => _logger;

        internal new void RescanAndUpdateIndex(
            string skillsDirectory,
            IReadOnlyList<ResolvedExternalSource> serverFeedSources,
            IReadOnlyList<ResolvedExternalSource> externalSources,
            string logLabel)
            => base.RescanAndUpdateIndex(skillsDirectory, serverFeedSources, externalSources, logLabel);

        internal async Task SyncFeedAsync(
            SkillFeedSource feed, NetclawPaths paths, TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            var feedDir = paths.ServerFeedDirectory(feed.Name);
            Directory.CreateDirectory(feedDir);

            var syncState = SkillSyncHelpers.ReadSyncState(
                paths.ServerFeedSyncStatePath(feed.Name), _logger);
            var now = timeProvider.GetUtcNow();

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
            var feedTimeout = TimeSpan.FromSeconds(feed.TimeoutSeconds);

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
                    var resources = entry.Resources?.Select(
                        r => new SkillResourceDescriptor(r.Path, r.Url, NormalizeDigest(r.Digest))).ToList();

                    var synced = await SyncSingleSkillAsync(
                        httpClient,
                        entry.Name,
                        version,
                        entry.Url,
                        digestHex,
                        resources,
                        feedDir,
                        syncState,
                        now,
                        feedTimeout,
                        logContext: $" from feed '{feed.Name}'",
                        cancellationToken);

                    if (synced)
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
                SkillSyncHelpers.WriteSyncState(
                    paths.ServerFeedSyncStatePath(feed.Name), syncState);
            }
        }

        private static HttpClient CreateHttpClientForFeed(SkillFeedSource feed)
        {
            var client = new HttpClient();
            if (feed.ApiKey is { Value: { } apiKey } && !string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
            }
            return client;
        }
    }
}
