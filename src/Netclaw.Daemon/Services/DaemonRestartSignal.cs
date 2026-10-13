// -----------------------------------------------------------------------
// <copyright file="DaemonRestartSignal.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Process-lifetime flag coordinating config-triggered restarts between
/// <see cref="ConfigWatcherService"/> (writer) and the outer restart loop
/// in Program.cs (reader). Created once in Program.cs, registered into
/// each host iteration's DI container.
/// </summary>
public sealed class DaemonRestartSignal
{
    private volatile bool _restartRequested;
    private int _generation;
    private readonly object _pluginSyncGate = new();
    private readonly HashSet<string> _pendingPluginIds = new(StringComparer.Ordinal);
    private string? _activeConfigHash;
    private string? _pendingConfigHash;
    private bool _scopeInvalid;

    public bool RestartRequested => _restartRequested;

    public void RequestRestart() => _restartRequested = true;

    public void Reset() => _restartRequested = false;

    /// <summary>
    /// Monotonically-increasing count of host (re)starts this process has driven.
    /// The outer restart loop calls <see cref="AdvanceGeneration"/> once per iteration
    /// and the value is surfaced on the anonymous <c>/api/health/ready</c> response so
    /// the init wizard can tell the reloaded daemon apart from the still-draining
    /// pre-restart one (#1302). A counter is used rather than a wall-clock start time
    /// because it is immune to clock step-back (NTP correction, VM resume) — a step-back
    /// could otherwise make a genuine restart look stale forever and time the wizard out.
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Advances <see cref="Generation"/>. Called once per restart-loop iteration.</summary>
    public void AdvanceGeneration() => Interlocked.Increment(ref _generation);

    internal void RecordPluginConfigChange(string sourceId, string? previousHash, string currentHash)
    {
        lock (_pluginSyncGate)
        {
            if (_scopeInvalid)
                return;

            var expectedHash = _pendingPluginIds.Count == 0 ? _activeConfigHash : _pendingConfigHash;
            if (!string.Equals(previousHash, expectedHash, StringComparison.Ordinal))
            {
                _scopeInvalid = true;
                _pendingPluginIds.Clear();
                return;
            }

            _pendingConfigHash = currentHash;
            _pendingPluginIds.Add(sourceId);
        }
    }

    internal (IReadOnlyList<string>? PluginIds, bool ScopeInvalid) TakePluginStartupScope(string configPath)
    {
        var currentHash = HashConfigFile(configPath);
        lock (_pluginSyncGate)
        {
            var invalid = _scopeInvalid
                || (_pendingPluginIds.Count > 0
                    && !string.Equals(_pendingConfigHash, currentHash, StringComparison.Ordinal));
            IReadOnlyList<string>? pluginIds = !invalid && _pendingPluginIds.Count > 0
                ? _pendingPluginIds.ToArray()
                : null;
            _activeConfigHash = currentHash;
            _pendingConfigHash = null;
            _pendingPluginIds.Clear();
            _scopeInvalid = false;
            return (pluginIds, invalid);
        }
    }

    internal static string? HashConfigFile(string configPath)
        => File.Exists(configPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath)))
            : null;

    internal static string HashConfigText(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
