// -----------------------------------------------------------------------
// <copyright file="DaemonRestartSignal.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
}
