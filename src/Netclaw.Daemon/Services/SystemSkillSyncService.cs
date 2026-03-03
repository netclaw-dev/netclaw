using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Syncs system skills from the feed CDN at daemon startup.
/// Runs after <see cref="Program.CopyBuiltInSkills"/> seeds offline defaults.
/// Never blocks startup on network — if the feed is unreachable, the daemon
/// starts with whatever skills are already on disk.
/// </summary>
internal sealed class SystemSkillSyncService : IHostedService
{
    private readonly HttpClient _httpClient;
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _skillRegistry;
    private readonly SkillIndexContextLayer _skillIndexLayer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SystemSkillSyncService> _logger;
    private readonly string _daemonVersion;

    public SystemSkillSyncService(
        HttpClient httpClient,
        NetclawPaths paths,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        TimeProvider timeProvider,
        ILogger<SystemSkillSyncService> logger)
        : this(httpClient, paths, skillRegistry, skillIndexLayer, timeProvider, logger, BuildInfo.Version)
    {
    }

    // Internal constructor for testing — allows injecting a fake daemon version
    internal SystemSkillSyncService(
        HttpClient httpClient,
        NetclawPaths paths,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        TimeProvider timeProvider,
        ILogger<SystemSkillSyncService> logger,
        string daemonVersion)
    {
        _httpClient = httpClient;
        _paths = paths;
        _skillRegistry = skillRegistry;
        _skillIndexLayer = skillIndexLayer;
        _timeProvider = timeProvider;
        _logger = logger;
        _daemonVersion = daemonVersion;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_paths.SystemSkillsDirectory);

            MigrateFlatSkills();

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

    /// <summary>
    /// One-time migration: move built-in skills from flat <c>skills/*.md</c> into
    /// <c>skills/.system/</c> and create an initial sync state.
    /// </summary>
    private void MigrateFlatSkills()
    {
        var builtInNames = new[] { "identity-management", "memorizer-usage", "self-diagnostics" };
        var migrated = false;

        foreach (var name in builtInNames)
        {
            var flatPath = Path.Combine(_paths.SkillsDirectory, $"{name}.md");
            var systemPath = Path.Combine(_paths.SystemSkillsDirectory, $"{name}.md");

            if (File.Exists(flatPath) && !File.Exists(systemPath))
            {
                File.Move(flatPath, systemPath);
                _logger.LogInformation("Migrated skill {SkillName} from skills/ to skills/.system/", name);
                migrated = true;
            }
        }

        if (migrated)
            _logger.LogInformation("Completed one-time migration of built-in skills to .system/ directory");
    }

    private SkillSyncState ReadSyncState()
    {
        if (!File.Exists(_paths.SkillSyncStatePath))
            return new SkillSyncState();

        try
        {
            var json = File.ReadAllText(_paths.SkillSyncStatePath);
            return JsonSerializer.Deserialize<SkillSyncState>(json) ?? new SkillSyncState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read sync state — starting fresh");
            return new SkillSyncState();
        }
    }

    private void WriteSyncState(SkillSyncState state)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_paths.SkillSyncStatePath, json);
    }

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

            // Download the skill
            try
            {
                var content = await DownloadSkillAsync(entry, cancellationToken);
                if (content is null)
                    continue;

                var targetPath = Path.Combine(_paths.SystemSkillsDirectory, $"{entry.Name}.md");
                await File.WriteAllTextAsync(targetPath, content, cancellationToken);

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

    private async Task<string?> DownloadSkillAsync(SkillFeedEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(FeedConstants.FeedHttpTimeout);

            var content = await _httpClient.GetStringAsync(entry.Url, cts.Token);

            // Verify SHA-256
            var hash = ComputeSha256(content);
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "SHA-256 mismatch for skill {SkillName}: expected {Expected}, got {Actual}",
                    entry.Name, entry.Sha256, hash);
                return null;
            }

            return content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Download timed out for skill {SkillName}", entry.Name);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Download failed for skill {SkillName}: {Message}", entry.Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Re-scans the entire skills directory and rebuilds the registry + context layer.
    /// Called after sync completes (or fails) to ensure the agent sees current skills.
    /// </summary>
    private void RescanAndUpdateIndex()
    {
        _skillRegistry.Clear();
        foreach (var skill in SkillScanner.Scan(_paths.SkillsDirectory))
            _skillRegistry.Register(skill);

        _skillIndexLayer.Update(_skillRegistry.GenerateCompressedIndex());
        _logger.LogInformation("Skill index updated ({SkillCount} skills)", _skillRegistry.GetAll().Count);
    }

    internal static string ComputeSha256(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Returns true if <paramref name="current"/> >= <paramref name="minimum"/>.
    /// Uses simple semver major.minor.patch comparison.
    /// </summary>
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
