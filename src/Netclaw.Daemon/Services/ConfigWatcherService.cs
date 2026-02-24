using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Monitors <c>~/.netclaw/config/</c> for changes to <c>netclaw.json</c> and
/// <c>secrets.json</c>. Debounces file system events and validates new config
/// before applying. See SPEC-011 §Configuration Hot-Reload.
///
/// <para>
/// Single reload trigger: all config changes go to disk first (TUI wizard,
/// manual editing, agent self-configuration). This watcher is the only
/// mechanism that triggers config reload — there is no in-memory config
/// mutation path.
/// </para>
/// </summary>
public sealed class ConfigWatcherService : IHostedService, IDisposable
{
    private readonly NetclawPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConfigWatcherService> _logger;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(500);

    public ConfigWatcherService(
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<ConfigWatcherService> logger)
    {
        _paths = paths;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath);
        if (configDir is null || !Directory.Exists(configDir))
        {
            _logger.LogWarning("Config directory does not exist: {ConfigDir}. Hot-reload disabled.", configDir);
            return Task.CompletedTask;
        }

        _watcher = new FileSystemWatcher(configDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileDeleted;

        _logger.LogInformation("Config hot-reload watching: {ConfigDir}", configDir);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        _debounceCts?.Cancel();
        return Task.CompletedTask;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsWatchedFile(e.Name))
            return;

        _logger.LogDebug("Config file changed: {FileName}", e.Name);
        ScheduleReload();
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsWatchedFile(e.Name))
            return;

        _logger.LogWarning("Config file deleted: {FileName}. Keeping current config.", e.Name);
    }

    private void ScheduleReload()
    {
        // Cancel any pending debounce timer and start a new one
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceInterval, token);
                if (token.IsCancellationRequested) return;

                ApplyReload();
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Config reload debounce cancelled by newer event");
            }
        }, CancellationToken.None);
    }

    private void ApplyReload()
    {
        try
        {
            // TODO: Read and validate new configuration
            // TODO: Rebuild IChatClientProvider on valid change
            // TODO: Notify actor system of config changes via Akka pub/sub
            // TODO: Log validation errors on invalid change, preserve previous config

            _logger.LogInformation(
                "[{Timestamp:o}] Config reload triggered (validation not yet implemented)",
                _timeProvider.GetUtcNow());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Config reload failed. Keeping previous config.");
        }
    }

    private static bool IsWatchedFile(string? fileName) =>
        fileName is "netclaw.json" or "secrets.json";

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceCts?.Dispose();
    }
}
