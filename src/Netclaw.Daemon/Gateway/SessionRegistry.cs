using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Singleton service managing the lifecycle of SignalR-connected sessions.
/// Bridges <see cref="SessionHub"/> (transient per-invocation) to per-session
/// <see cref="SignalRSessionActor"/> instances via the <c>signalr-gateway</c> actor.
/// </summary>
/// <remarks>
/// Stream materialization is now delegated to <see cref="SignalRSessionActor"/>, which
/// creates a <see cref="ActorMaterializer"/> scoped to its own actor context. This ensures
/// all Akka.Streams stage actors are children of the session actor and are automatically
/// stopped when the session ends — eliminating the StreamSupervisor actor accumulation
/// that previously occurred when streams were materialized at the system level.
/// </remarks>
public sealed class SessionRegistry
{
    private readonly IRequiredActor<SignalRGatewayActorKey> _gatewayProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionRegistry> _logger;

    // session ID → channel type (retained for re-create on re-materialization)
    private readonly Dictionary<SessionId, string> _knownSessions = new();
    private readonly SemaphoreSlim _sessionMutationGate = new(1, 1);

    private readonly SessionConnectionMap _connections = new();

    public SessionRegistry(
        IRequiredActor<SignalRGatewayActorKey> gatewayProvider,
        TimeProvider timeProvider,
        ILogger<SessionRegistry> logger)
    {
        _gatewayProvider = gatewayProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new session for the given SignalR connection.
    /// </summary>
    public async Task<string> CreateSessionAsync(string connectionId, string channelType)
    {
        var callerConnectionId = ParseConnectionId(connectionId);

        await _sessionMutationGate.WaitAsync();
        try
        {
            return await CreateSessionCoreAsync(callerConnectionId, channelType);
        }
        finally
        {
            _sessionMutationGate.Release();
        }
    }

    private async Task<string> CreateSessionCoreAsync(
        SignalRConnectionId callerConnectionId,
        string channelType)
    {
        var sessionId = new SessionId($"signalr/{Guid.NewGuid():N}");

        _knownSessions[sessionId] = channelType;
        var previousSessionId = _connections.BindNewSession(sessionId, callerConnectionId);

        // Remove displaced session from known sessions
        if (previousSessionId.HasValue)
        {
            _knownSessions.Remove(previousSessionId.Value);
            var gateway = await _gatewayProvider.GetAsync();
            gateway.Tell(new ShutdownSignalRSession(previousSessionId.Value));
        }

        var gw = await _gatewayProvider.GetAsync();
        gw.Tell(new StartSignalRSession(sessionId, channelType, callerConnectionId));

        _logger.LogDebug(
            "Session {SessionId} created and bound to connection {ConnectionId}.",
            sessionId.Value, callerConnectionId.Value);

        return sessionId.Value;
    }

    public async Task<SessionEnsureResultDto> EnsureSessionAsync(
        string connectionId,
        string? sessionId,
        string channelType)
    {
        var callerConnectionId = ParseConnectionId(connectionId);

        await _sessionMutationGate.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var requestedSessionId = ParseSessionId(sessionId);

                if (_knownSessions.ContainsKey(requestedSessionId))
                {
                    // Session exists — attach the new connection; actor handles re-init internally
                    _connections.AttachSession(requestedSessionId, callerConnectionId);

                    var gateway = await _gatewayProvider.GetAsync();
                    gateway.Tell(new AttachSignalRConnection(requestedSessionId, callerConnectionId));

                    _logger.LogDebug(
                        "Attaching connection {ConnectionId} to existing session {SessionId}.",
                        callerConnectionId.Value, requestedSessionId.Value);

                    return new SessionEnsureResultDto
                    {
                        SessionId = requestedSessionId.Value,
                        Created = false
                    };
                }

                // Session ID provided but unknown — create a fresh session binding
                _knownSessions[requestedSessionId] = channelType;
                _connections.BindNewSession(requestedSessionId, callerConnectionId);

                var gw2 = await _gatewayProvider.GetAsync();
                gw2.Tell(new StartSignalRSession(requestedSessionId, channelType, callerConnectionId));

                return new SessionEnsureResultDto
                {
                    SessionId = requestedSessionId.Value,
                    Created = false
                };
            }

            var createdSessionId = await CreateSessionCoreAsync(callerConnectionId, channelType);
            return new SessionEnsureResultDto
            {
                SessionId = createdSessionId,
                Created = true
            };
        }
        finally
        {
            _sessionMutationGate.Release();
        }
    }

    /// <summary>
    /// Attaches the current SignalR connection to an existing session.
    /// Supports reconnect flows where connection IDs rotate.
    /// </summary>
    public async Task AttachSessionAsync(string connectionId, string sessionId)
    {
        var callerConnectionId = ParseConnectionId(connectionId);
        var requestedSessionId = ParseSessionId(sessionId);

        await _sessionMutationGate.WaitAsync();
        try
        {
            if (!_knownSessions.TryGetValue(requestedSessionId, out var channelType))
                throw new HubException($"Session '{sessionId}' not found.");

            _connections.AttachSession(requestedSessionId, callerConnectionId);

            var gateway = await _gatewayProvider.GetAsync();
            gateway.Tell(new AttachSignalRConnection(requestedSessionId, callerConnectionId));

            _logger.LogDebug(
                "Attaching connection {ConnectionId} to session {SessionId}.",
                callerConnectionId.Value, requestedSessionId.Value);
        }
        finally
        {
            _sessionMutationGate.Release();
        }
    }

    /// <summary>
    /// Pushes a user message into an existing session's input queue.
    /// </summary>
    public async Task SendMessageAsync(string connectionId, string sessionId, string text)
    {
        var callerConnectionId = ParseConnectionId(connectionId);
        var requestedSessionId = ParseSessionId(sessionId);

        if (!_connections.TryGetSessionForConnection(callerConnectionId, out var attachedSessionId))
            throw new HubException($"Session '{sessionId}' is not attached to this connection.");

        if (!attachedSessionId.Equals(requestedSessionId))
            throw new HubException($"Session '{sessionId}' is not attached to this connection.");

        if (!_knownSessions.ContainsKey(attachedSessionId))
            throw new HubException($"Session '{sessionId}' not found.");

        var signalrMessageId = $"signalr:{callerConnectionId.Value}:{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}:{Guid.NewGuid():N}";
        if (signalrMessageId.Length > 128)
            signalrMessageId = signalrMessageId[..128];

        var input = new ChannelInput
        {
            SenderId = "signalr-user",
            MessageId = signalrMessageId,
            Contents = [new TextContent(text)],
            ReceivedAt = _timeProvider.GetUtcNow()
        };

        var gateway = await _gatewayProvider.GetAsync();
        gateway.Tell(new EnqueueSignalRInput(attachedSessionId, input));
    }

    /// <summary>
    /// Cleans up session state when a SignalR connection disconnects.
    /// Session is intentionally preserved for reconnection.
    /// </summary>
    public Task OnDisconnectedAsync(string connectionId)
    {
        var parsed = ParseConnectionId(connectionId);

        if (_connections.TryGetSessionForConnection(parsed, out var sessionId))
        {
            _logger.LogDebug(
                "Connection {ConnectionId} disconnected; detached from session {SessionId}.",
                connectionId, sessionId.Value);
        }

        _connections.Disconnect(parsed);

        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await _sessionMutationGate.WaitAsync(cancellationToken);
        try
        {
            var sessions = _knownSessions.Keys.ToArray();
            _knownSessions.Clear();
            _connections.Clear();

            if (sessions.Length == 0)
                return;

            IActorRef gateway;
            try
            {
                gateway = await _gatewayProvider.GetAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timed out resolving gateway actor during shutdown; {Count} session(s) may not have been notified.", sessions.Length);
                return;
            }

            foreach (var sessionId in sessions)
                gateway.Tell(new ShutdownSignalRSession(sessionId));

            _logger.LogDebug("Shutdown signals sent to {Count} session(s).", sessions.Length);
        }
        finally
        {
            _sessionMutationGate.Release();
        }
    }

    private static SignalRConnectionId ParseConnectionId(string connectionId)
    {
        try
        {
            return SignalRConnectionId.Create(connectionId);
        }
        catch (ArgumentException)
        {
            throw new HubException("Connection ID is invalid.");
        }
    }

    private static SessionId ParseSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new HubException("Session ID cannot be empty.");

        return new SessionId(sessionId);
    }
}
