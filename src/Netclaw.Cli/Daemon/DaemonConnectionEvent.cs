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
