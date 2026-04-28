using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Watches all skill directories (native + external sources) for file changes
/// and triggers a debounced rescan to keep the in-memory skill registry
/// up to date without requiring a daemon restart.
/// </summary>
public sealed class SkillDirectoryWatcherService : BackgroundService
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);

    private readonly NetclawPaths _paths;
    private readonly IReadOnlyList<ResolvedExternalSource> _serverFeedSources;
    private readonly IReadOnlyList<ResolvedExternalSource> _externalSources;
    private readonly SkillRegistry _registry;
    private readonly SkillIndexContextLayer _indexLayer;
    private readonly ILogger<SkillDirectoryWatcherService> _logger;

    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _debounceTimer;
    private int _rescanInProgress;
    private int _pendingRescan;

    public SkillDirectoryWatcherService(
        NetclawPaths paths,
        [Microsoft.Extensions.DependencyInjection.FromKeyedServices("server-feeds")]
        IReadOnlyList<ResolvedExternalSource> serverFeedSources,
        IReadOnlyList<ResolvedExternalSource> externalSources,
        SkillRegistry registry,
        SkillIndexContextLayer indexLayer,
        ILogger<SkillDirectoryWatcherService> logger)
    {
        _paths = paths;
        _serverFeedSources = serverFeedSources;
        _externalSources = externalSources;
        _registry = registry;
        _indexLayer = indexLayer;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _debounceTimer = new Timer(_ => OnDebounceTimerFired(), null, Timeout.Infinite, Timeout.Infinite);

        // Watch the native skills directory
        TryCreateWatcher(_paths.SkillsDirectory, "native");

        // Watch each external source directory. A single source may cover multiple
        // paths (e.g. claude-code = ~/.claude/skills + ~/.claude/commands + one
        // path per installed plugin marketplace under
        // ~/.claude/plugins/marketplaces/*/skills/).
        foreach (var source in _externalSources)
        {
            foreach (var path in source.Paths)
            {
                TryCreateWatcher(path, source.Name);
            }
        }

        return Task.CompletedTask;
    }

    private void TryCreateWatcher(string directory, string label)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning(
                "Skill directory does not exist, skipping watcher: {Directory} ({Label})",
                directory, label);
            return;
        }

        var watcher = new FileSystemWatcher(directory)
        {
            Filter = "*.md",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };

        watcher.Created += OnFileEvent;
        watcher.Changed += OnFileEvent;
        watcher.Deleted += OnFileEvent;
        watcher.Renamed += OnRenameEvent;
        watcher.Error += OnWatcherError;

        _watchers.Add(watcher);
        _logger.LogInformation("Watching skill directory: {Directory} ({Label})", directory, label);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (ShouldIgnore(e.FullPath))
                return;

            _logger.LogDebug("Skill file {ChangeType}: {Path}", e.ChangeType, e.FullPath);
            ResetDebounceTimer();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in skill watcher file callback for {Path}", e.FullPath);
        }
    }

    private void OnRenameEvent(object sender, RenamedEventArgs e)
    {
        try
        {
            if (ShouldIgnore(e.FullPath) && ShouldIgnore(e.OldFullPath))
                return;

            _logger.LogDebug("Skill file renamed: {OldPath} -> {Path}", e.OldFullPath, e.FullPath);
            ResetDebounceTimer();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in skill watcher rename callback for {Path}", e.FullPath);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        try
        {
            _logger.LogWarning(e.GetException(), "FileSystemWatcher error — triggering recovery rescan");
            ResetDebounceTimer();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in skill watcher error callback");
        }
    }

    private void ResetDebounceTimer()
    {
        _debounceTimer?.Change(DebounceInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnDebounceTimerFired()
    {
        // Prevent overlapping rescans — if one is running, mark pending so it retries after
        if (Interlocked.CompareExchange(ref _rescanInProgress, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _pendingRescan, 1);
            _logger.LogDebug("Rescan already in progress, marked pending");
            return;
        }

        try
        {
            _logger.LogDebug("Debounce timer fired, starting skill rescan");

            var result = SkillScanner.ScanAndMerge(
                _paths.SkillsDirectory, _serverFeedSources, _externalSources);
            SkillRegistryUpdater.ApplyMergedScanResult(
                _registry, _indexLayer, result, _paths.SkillsDirectory, _externalSources);

            _logger.LogInformation(
                "Skill directory rescan complete: {SkillCount} skills loaded, {IssueCount} issues",
                result.AcceptedSkills.Count, result.Issues.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Skill directory rescan failed");
        }
        finally
        {
            Interlocked.Exchange(ref _rescanInProgress, 0);

            // If changes arrived while we were scanning, schedule another debounced rescan
            if (Interlocked.CompareExchange(ref _pendingRescan, 0, 1) == 1)
            {
                _logger.LogDebug("Pending rescan detected, scheduling another debounce cycle");
                ResetDebounceTimer();
            }
        }
    }

    private static bool ShouldIgnore(string fullPath)
    {
        // Ignore staging directories and temp files used by atomic write operations
        return fullPath.Contains($"{Path.DirectorySeparatorChar}.staging{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || fullPath.Contains($"{Path.AltDirectorySeparatorChar}.staging{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            || fullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    public override void Dispose()
    {
        _debounceTimer?.Dispose();

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();

        base.Dispose();
    }
}
