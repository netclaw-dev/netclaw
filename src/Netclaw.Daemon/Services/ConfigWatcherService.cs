// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Monitors <c>~/.netclaw/config/</c> for changes to <c>netclaw.json</c>.
/// Debounces file system events and validates new config before applying.
/// See SPEC-011 §Configuration Restart Coordination.
///
/// <para>
/// Single reload trigger: all config changes go to disk first (TUI wizard,
/// manual editing, agent self-configuration). This watcher is the only
/// mechanism that triggers config reload — there is no in-memory config
/// mutation path.
/// </para>
/// <para>
/// <see cref="DaemonConfig"/> properties (bind address, exposure mode) are
/// excluded from hot-reload. If those values change, a warning is logged and
/// the operator must restart the daemon manually.
/// </para>
/// </summary>
public sealed class ConfigWatcherService : IHostedService, IDisposable
{
    private readonly NetclawPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly IDaemonRestartCoordinator _restartCoordinator;
    private readonly DaemonConfig _currentDaemonConfig;
    private readonly ILogger<ConfigWatcherService> _logger;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(500);
    private readonly object _debounceLock = new();
    private Task _pendingReload = Task.CompletedTask;

    /// <summary>
    /// The in-flight debounced reload, or a completed task when none is pending.
    /// Lets tests await the reload deterministically instead of polling.
    /// </summary>
    internal Task PendingReload
    {
        get { lock (_debounceLock) { return _pendingReload; } }
    }

    /// <summary>The debounce window applied before a detected change is reloaded.</summary>
    internal TimeSpan DebounceInterval => _debounceInterval;

    public ConfigWatcherService(
        NetclawPaths paths,
        TimeProvider timeProvider,
        IDaemonRestartCoordinator restartCoordinator,
        DaemonConfig currentDaemonConfig,
        ILogger<ConfigWatcherService> logger)
    {
        _paths = paths;
        _timeProvider = timeProvider;
        _restartCoordinator = restartCoordinator;
        _currentDaemonConfig = currentDaemonConfig;
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
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;

        _logger.LogInformation("Config hot-reload watching: {ConfigDir}", configDir);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        _debounceCts?.Cancel();
        return Task.CompletedTask;
    }

    internal void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (!IsWatchedFile(e.Name))
                return;

            _logger.LogDebug("Config file changed: {FileName}", e.Name);
            ScheduleReload();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in config watcher Changed callback for {FileName}", e.Name);
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (!IsWatchedFile(e.Name))
                return;

            _logger.LogWarning("Config file deleted: {FileName}. Keeping current config.", e.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in config watcher Deleted callback for {FileName}", e.Name);
        }
    }

    internal void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            // Rename INTO our watched filename = atomic-replace write (write-temp + rename).
            if (IsWatchedFile(e.Name))
            {
                _logger.LogDebug("Config file rename detected: {OldName} -> {NewName}", e.OldName, e.Name);
                ScheduleReload();
                return;
            }

            // Rename OUT of our watched filename = treat like a delete.
            if (IsWatchedFile(e.OldName))
            {
                _logger.LogWarning("Config file renamed away: {OldName} -> {NewName}. Keeping current config.", e.OldName, e.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in config watcher Renamed callback for {FileName}", e.Name);
        }
    }

    private void ScheduleReload()
    {
        // FileSystemWatcher raises events on thread-pool threads and may deliver
        // them concurrently. The cancel-and-replace of the debounce CTS must be
        // atomic: without the lock a concurrent event can leave a debounce loop
        // awaiting a CTS that no later event will ever cancel, double-firing the
        // reload.
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            _pendingReload = RunDebouncedReloadAsync(_debounceCts.Token);
        }
    }

    private async Task RunDebouncedReloadAsync(CancellationToken token)
    {
        try
        {
            // Debounce on the injected TimeProvider so tests can virtualize it.
            await Task.Delay(_debounceInterval, _timeProvider, token);
            if (token.IsCancellationRequested)
                return;

            await ApplyReloadAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Config reload debounce cancelled by newer event");
        }
    }

    internal async Task ApplyReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "[{Timestamp:o}] Config change detected, validating before restart...",
                _timeProvider.GetUtcNow());

            // Validate JSON structure of the watched config file before triggering restart.
            // Full semantic validation happens during the next startup cycle.
            if (!ValidateConfigJson(_paths.NetclawConfigPath))
            {
                _logger.LogWarning("Config validation failed. Keeping current config — no restart.");
                return;
            }

            // Daemon section changes (bind address, exposure mode) are not applied via
            // hot-reload — they affect network infrastructure and require a clean restart.
            // Warn the operator and skip; all other config changes will take effect on
            // the next manual restart.
            DaemonConfig newDaemonConfig;
            try
            {
                newDaemonConfig = ReadDaemonConfigFromFile(_paths.NetclawConfigPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read Daemon section from updated config; skipping hot-reload.");
                return;
            }

            if (newDaemonConfig != _currentDaemonConfig)
            {
                _logger.LogWarning(
                    "Daemon configuration changed (bind address or exposure mode). " +
                    "These settings are not applied via hot-reload — restart the daemon manually to apply the new network configuration.");
                return;
            }

            _logger.LogInformation("Config valid. Starting coordinated daemon restart.");
            await _restartCoordinator.RequestConfigRestartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Config reload failed. Keeping previous config.");
        }
    }

    internal static DaemonConfig ReadDaemonConfigFromFile(string path)
    {
        if (!File.Exists(path))
            return new DaemonConfig();

        var config = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();
        return DaemonConfig.BindFromConfiguration(config.GetSection("Daemon"));
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
