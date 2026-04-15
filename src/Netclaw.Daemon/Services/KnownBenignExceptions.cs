namespace Netclaw.Daemon.Services;

/// <summary>
/// Predicates for identifying unobserved task exceptions that are known to be
/// benign and must not be reported as daemon crashes.
///
/// <para>
/// Each entry documents (a) which upstream component produces the exception,
/// (b) why it is safe to observe without a crash report, and (c) the delete
/// criteria for removing the filter once the upstream bug is fixed.
/// </para>
/// </summary>
internal static class KnownBenignExceptions
{
    private const string ReconnectingWebSocketConnectMarker =
        "SlackNet.ReconnectingWebSocket.Connect";

    /// <summary>
    /// SlackNet 0.17.10's <c>ReconnectingWebSocket.Dispose()</c> cancels and
    /// synchronously disposes its internal <see cref="System.Threading.CancellationTokenSource"/>
    /// while its background reconnect loop may still re-enter <c>Connect()</c>,
    /// which reads <c>_disposed.Token</c> and throws
    /// <see cref="System.ObjectDisposedException"/>. The task is fire-and-forget
    /// from <c>ConnectInternal</c>, so the exception surfaces on the finalizer
    /// thread as an unobserved task exception.
    ///
    /// <para>
    /// Netclaw's hot-reload restart path (<see cref="DaemonRestartCoordinator"/>)
    /// disposes the DI container between host iterations, which is what triggers
    /// the race in the first place. By the time the exception fires, the old
    /// Slack client has already been torn down and replaced, so there is no
    /// state for Netclaw to salvage and no user-visible behavior to protect.
    /// </para>
    ///
    /// <para>
    /// <b>Delete criteria:</b> remove this filter after bumping the SlackNet
    /// package past the version that fixes the race upstream.
    /// </para>
    /// </summary>
    public static bool IsSlackNetReconnectingWebSocketDisposeRace(Exception? exception)
    {
        if (exception is null)
            return false;

        var candidate = UnwrapAggregate(exception);
        if (candidate is not ObjectDisposedException)
            return false;

        var stackTrace = candidate.StackTrace;
        return stackTrace is not null
            && stackTrace.Contains(ReconnectingWebSocketConnectMarker, StringComparison.Ordinal);
    }

    private static Exception UnwrapAggregate(Exception exception)
    {
        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            return aggregate.InnerExceptions[0];

        return exception;
    }
}
