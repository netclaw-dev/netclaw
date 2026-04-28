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

    public bool RestartRequested => _restartRequested;

    public void RequestRestart() => _restartRequested = true;

    public void Reset() => _restartRequested = false;
}
