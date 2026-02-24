using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Thread-safe bidirectional mapping between SignalR connection IDs and
/// session IDs.
/// </summary>
internal sealed class SessionConnectionMap
{
    private readonly object _gate = new();
    private readonly Dictionary<SessionId, SignalRConnectionId> _sessionToConnection = [];
    private readonly Dictionary<SignalRConnectionId, SessionId> _connectionToSession = [];

    /// <summary>
    /// Binds a newly created session to a connection. If that connection was
    /// already attached to a previous session, returns the replaced session ID.
    /// </summary>
    public SessionId? BindNewSession(SessionId sessionId, SignalRConnectionId connectionId)
    {
        lock (_gate)
        {
            RemoveSessionInternal(sessionId);

            SessionId? replacedSessionId = null;
            if (_connectionToSession.TryGetValue(connectionId, out var existingSessionId))
            {
                replacedSessionId = existingSessionId;
                RemoveSessionInternal(existingSessionId);
            }

            _sessionToConnection[sessionId] = connectionId;
            _connectionToSession[connectionId] = sessionId;
            return replacedSessionId;
        }
    }

    /// <summary>
    /// Attaches an existing session to a connection, detaching any previous
    /// connection/session pairings that would conflict.
    /// </summary>
    public void AttachSession(SessionId sessionId, SignalRConnectionId connectionId)
    {
        lock (_gate)
        {
            RemoveConnectionInternal(connectionId);
            RemoveSessionInternal(sessionId);

            _sessionToConnection[sessionId] = connectionId;
            _connectionToSession[connectionId] = sessionId;
        }
    }

    public bool IsAttached(SignalRConnectionId connectionId, SessionId sessionId)
    {
        lock (_gate)
        {
            return _connectionToSession.TryGetValue(connectionId, out var attachedSessionId)
                && attachedSessionId.Equals(sessionId);
        }
    }

    public bool TryGetConnectionForSession(SessionId sessionId, out SignalRConnectionId connectionId)
    {
        lock (_gate)
        {
            if (_sessionToConnection.TryGetValue(sessionId, out var foundConnection))
            {
                connectionId = foundConnection;
                return true;
            }

            connectionId = default;
            return false;
        }
    }

    public void Disconnect(SignalRConnectionId connectionId)
    {
        lock (_gate)
            RemoveConnectionInternal(connectionId);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sessionToConnection.Clear();
            _connectionToSession.Clear();
        }
    }

    private void RemoveConnectionInternal(SignalRConnectionId connectionId)
    {
        if (_connectionToSession.TryGetValue(connectionId, out var sessionId))
        {
            _connectionToSession.Remove(connectionId);
            _sessionToConnection.Remove(sessionId);
        }
    }

    private void RemoveSessionInternal(SessionId sessionId)
    {
        if (_sessionToConnection.TryGetValue(sessionId, out var connectionId))
        {
            _sessionToConnection.Remove(sessionId);
            _connectionToSession.Remove(connectionId);
        }
    }
}
