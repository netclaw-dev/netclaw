using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Monitors <c>~/.netclaw/config/</c> for changes to <c>netclaw.json</c>.
/// Debounces file system events and validates new config before applying.
/// See SPEC-011 §Configuration Hot-Reload.
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
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly DaemonRestartSignal _restartSignal;
    private readonly DaemonLifecycleNotifier _lifecycleNotifier;
    private readonly ILogger<ConfigWatcherService> _logger;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(500);

    public ConfigWatcherService(
        NetclawPaths paths,
        TimeProvider timeProvider,
        IHostApplicationLifetime appLifetime,
        DaemonRestartSignal restartSignal,
        DaemonLifecycleNotifier lifecycleNotifier,
        ILogger<ConfigWatcherService> logger)
    {
        _paths = paths;
        _timeProvider = timeProvider;
        _appLifetime = appLifetime;
        _restartSignal = restartSignal;
        _lifecycleNotifier = lifecycleNotifier;
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

    internal void ApplyReload()
    {
        try
        {
            _logger.LogInformation(
                "[{Timestamp:o}] Config change detected, validating before restart...",
                _timeProvider.GetUtcNow());

            // Validate JSON structure of both config files before triggering restart.
            // Full semantic validation happens during the next startup cycle.
            if (!ValidateConfigJson(_paths.NetclawConfigPath))
            {
                _logger.LogWarning("Config validation failed. Keeping current config — no restart.");
                return;
            }

            _logger.LogInformation("Config valid. Requesting daemon restart.");
            _lifecycleNotifier.NotifyShutdown("config-reload");
            _restartSignal.RequestRestart();
            _appLifetime.StopApplication();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Config reload failed. Keeping previous config.");
        }
    }

    private bool ValidateConfigJson(string path)
    {
        if (!File.Exists(path))
            return true; // Missing files are OK — they're optional in the config chain

        try
        {
            var bytes = File.ReadAllBytes(path);
            var reader = new Utf8JsonReader(bytes);
            while (reader.Read()) { } // Walk the entire document to catch syntax errors
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Invalid JSON in {ConfigFile}: {Error}", path, ex.Message);
            return false;
        }
    }

    internal static bool IsWatchedFile(string? fileName) =>
        fileName is "netclaw.json";

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceCts?.Dispose();
    }
}
