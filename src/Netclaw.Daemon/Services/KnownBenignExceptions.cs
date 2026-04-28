// -----------------------------------------------------------------------
// <copyright file="KnownBenignExceptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Daemon.Services;

internal static class KnownBenignExceptions
{
    private const string ReconnectingWebSocketConnectMarker =
        "SlackNet.ReconnectingWebSocket.Connect";

    /// <summary>
    /// SlackNet 0.17.10 race: <c>ReconnectingWebSocket.Dispose()</c> cancels and
    /// synchronously disposes its internal CTS while the background reconnect
    /// loop may still re-enter <c>Connect()</c> and touch <c>_disposed.Token</c>.
    /// Surfaces as an unobserved task exception on Netclaw's hot-reload path,
    /// after the old Slack client has already been replaced.
    /// Delete after upgrading past the SlackNet version that fixes this upstream.
    /// </summary>
    public static bool IsSlackNetReconnectingWebSocketDisposeRace(Exception? exception)
    {
        if (exception is null)
            return false;

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                if (MatchesRace(inner))
                    return true;
            }

            return false;
        }

        return MatchesRace(exception);
    }

    private static bool MatchesRace(Exception exception)
    {
        if (exception is not ObjectDisposedException)
            return false;

        var stackTrace = exception.StackTrace;
        return stackTrace is not null
            && stackTrace.Contains(ReconnectingWebSocketConnectMarker, StringComparison.Ordinal);
    }
}
