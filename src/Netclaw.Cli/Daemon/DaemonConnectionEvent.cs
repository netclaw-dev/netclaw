// -----------------------------------------------------------------------
// <copyright file="DaemonConnectionEvent.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Daemon;

/// <summary>
/// Lifecycle states for the daemon SignalR connection, as surfaced by
/// <see cref="DaemonClient.ConnectionEvents"/>.
/// </summary>
public enum DaemonConnectionState
{
    /// <summary>An initial connection attempt is in progress.</summary>
    Connecting,

    /// <summary>The transport is connected and the session is attached.</summary>
    Connected,

    /// <summary>
    /// A reconnect is in progress — either SignalR's built-in auto-reconnect or
    /// the supervised <c>ReconnectLoopAsync</c>. Transient: a <see cref="Connected"/>
    /// or a terminal <see cref="Disconnected"/> follows.
    /// </summary>
    Reconnecting,

    /// <summary>
    /// The transport dropped and SignalR's built-in auto-reconnect gave up; the
    /// supervised reconnect loop is taking over. Transient — a <see cref="Reconnecting"/>
    /// follows within microseconds. Consumers should render this like
    /// <see cref="Reconnecting"/>, not as a failure.
    /// </summary>
    TransportClosed,

    /// <summary>
    /// Terminal failure: the supervised reconnect loop exhausted its retry budget.
    /// No further automatic recovery occurs without an explicit reconnect.
    /// </summary>
    Disconnected
}

public sealed record DaemonConnectionEvent(
    DaemonConnectionState State,
    string Endpoint,
    string Message,
    int? Attempt = null,
    int? MaxAttempts = null,
    int? SecondsUntilRetry = null);
