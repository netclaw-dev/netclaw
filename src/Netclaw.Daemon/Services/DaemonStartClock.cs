// -----------------------------------------------------------------------
// <copyright file="DaemonStartClock.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Daemon.Services;

/// <summary>
/// Captures the daemon's logical start time once at construction.
/// Register as a singleton and eagerly resolve so the timestamp
/// reflects actual startup, not first-request time.
/// </summary>
public sealed class DaemonStartClock(TimeProvider timeProvider)
{
    public DateTimeOffset StartedAt { get; } = timeProvider.GetUtcNow();
}
