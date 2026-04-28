// -----------------------------------------------------------------------
// <copyright file="DaemonConnectionEvent.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Daemon;

public enum DaemonConnectionState
{
    Connecting,
    Connected,
    Reconnecting,
    Disconnected
}

public sealed record DaemonConnectionEvent(
    DaemonConnectionState State,
    string Endpoint,
    string Message,
    int? Attempt = null,
    int? MaxAttempts = null,
    int? SecondsUntilRetry = null);
