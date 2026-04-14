namespace Netclaw.Configuration;

/// <summary>
/// Process-wide best-effort snapshot of the most recent session/turn context.
/// Updated by session actors so process-level crash handlers can include
/// actionable context in crash logs and alerts.
/// </summary>
public static class CrashContextSnapshot
{
    private static readonly object Gate = new();
    private static CrashTurnContext? _latest;

    public static void Update(
        string sessionId,
        string? turnId,
        string? messageId,
        string? channelType,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (Gate)
        {
            _latest = new CrashTurnContext(
                sessionId,
                turnId,
                messageId,
                channelType,
                observedAtUtc);
        }
    }

    public static CrashTurnContext? GetLatest()
    {
        lock (Gate)
        {
            return _latest;
        }
    }
}

public sealed record CrashTurnContext(
    string SessionId,
    string? TurnId,
    string? MessageId,
    string? ChannelType,
    DateTimeOffset ObservedAtUtc);
