// -----------------------------------------------------------------------
// <copyright file="SessionRegistry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Claims;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

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
    private readonly ISessionPipeline _pipeline;
    private readonly SessionIngressGate _ingressGate;
    private readonly ClaimsPrincipalMapper _mapper;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionRegistry> _logger;

    // session ID → channel type (retained for re-create on re-materialization)
    private readonly Dictionary<SessionId, Actors.Channels.ChannelType> _knownSessions = [];
    private readonly SemaphoreSlim _sessionMutationGate = new(1, 1);

    private readonly SessionConnectionMap _connections = new();

    public SessionRegistry(
        IRequiredActor<SignalRGatewayActorKey> gatewayProvider,
        ISessionPipeline pipeline,
        SessionIngressGate ingressGate,
        ClaimsPrincipalMapper mapper,
        TimeProvider timeProvider,
        ILogger<SessionRegistry> logger)
    {
        _gatewayProvider = gatewayProvider;
        _pipeline = pipeline;
        _ingressGate = ingressGate;
        _mapper = mapper;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new session for the given SignalR connection.
    /// </summary>
    public async Task<string> CreateSessionAsync(string connectionId, string channelType, ClaimsPrincipal? principal = null)
    {
        var callerConnectionId = ParseConnectionId(connectionId);
        var ct = ParseChannelType(channelType);

        await _sessionMutationGate.WaitAsync();
        try
        {
            ThrowIfIngressClosed();
            return await CreateSessionCoreAsync(callerConnectionId, ct);
        }
        finally
        {
            _sessionMutationGate.Release();
        }
    }

    private async Task<string> CreateSessionCoreAsync(
        SignalRConnectionId callerConnectionId,
        Actors.Channels.ChannelType channelType)
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
        string channelType,
        ClaimsPrincipal? principal = null)
    {
        var callerConnectionId = ParseConnectionId(connectionId);
        var ct = ParseChannelType(channelType);

        await _sessionMutationGate.WaitAsync();
        try
        {
            ThrowIfIngressClosed();

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

                    return new SessionEnsureResultDto(requestedSessionId.Value, Created: false);
                }

                // Session ID provided but unknown — create a fresh session binding
                _knownSessions[requestedSessionId] = ct;
                _connections.BindNewSession(requestedSessionId, callerConnectionId);

                var gw2 = await _gatewayProvider.GetAsync();
                gw2.Tell(new StartSignalRSession(requestedSessionId, ct, callerConnectionId));

                return new SessionEnsureResultDto(requestedSessionId.Value, Created: false);
            }

            var createdSessionId = await CreateSessionCoreAsync(callerConnectionId, ct);
            return new SessionEnsureResultDto(createdSessionId, Created: true);
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
    public async Task AttachSessionAsync(string connectionId, string sessionId, ClaimsPrincipal? principal = null)
    {
        var callerConnectionId = ParseConnectionId(connectionId);
        var requestedSessionId = ParseSessionId(sessionId);

        await _sessionMutationGate.WaitAsync();
        try
        {
            ThrowIfIngressClosed();

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
    public async Task SendMessageAsync(string connectionId, string sessionId, string text, ClaimsPrincipal? principal = null)
    {
        var callerConnectionId = ParseConnectionId(connectionId);
        var requestedSessionId = ParseSessionId(sessionId);

        if (!_connections.TryGetSessionForConnection(callerConnectionId, out var attachedSessionId))
            throw new HubException($"Session '{sessionId}' is not attached to this connection.");

        if (!attachedSessionId.Equals(requestedSessionId))
            throw new HubException($"Session '{sessionId}' is not attached to this connection.");

        if (!_knownSessions.ContainsKey(attachedSessionId))
            throw new HubException($"Session '{sessionId}' not found.");

        ThrowIfIngressClosed();

        var identity = _mapper.Map(principal);

        var signalrMessageId = $"signalr:{callerConnectionId.Value}:{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}:{Guid.NewGuid():N}";
        if (signalrMessageId.Length > 128)
            signalrMessageId = signalrMessageId[..128];

        var input = new ChannelInput
        {
            SenderId = identity.SenderId,
            MessageId = signalrMessageId,
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            Principal = identity.Principal,
            Provenance = new SourceProvenance(
                identity.Transport,
                PayloadTaint.Trusted)
            {
                SourceKind = new SourceKind("signalr")
            },
            Contents = [new TextContent(text)],
            ReceivedAt = _timeProvider.GetUtcNow(),
            DefaultDeliveryTarget = new ChannelDeliveryTargetInfo(
                ChannelType.Tui.ToWireValue(),
                "local_session",
                attachedSessionId.Value,
                attachedSessionId.Value)
        };

        var gateway = await _gatewayProvider.GetAsync();
        gateway.Tell(new EnqueueSignalRInput(attachedSessionId, input));
    }

    public async Task RespondToInteractionAsync(
        string connectionId,
        string sessionId,
        string callId,
        string selectedKey,
        ClaimsPrincipal? principal = null)
    {
        var callerConnectionId = ParseConnectionId(connectionId);
        var requestedSessionId = ParseSessionId(sessionId);

        if (!_connections.TryGetSessionForConnection(callerConnectionId, out var attachedSessionId))
            throw new HubException($"Session '{sessionId}' is not attached to this connection.");

        if (!attachedSessionId.Equals(requestedSessionId))
            throw new HubException($"Session '{sessionId}' is not attached to this connection.");

        if (!_knownSessions.ContainsKey(attachedSessionId))
            throw new HubException($"Session '{sessionId}' not found.");

        ThrowIfIngressClosed();

        var identity = _mapper.Map(principal);

        await _pipeline.SendFeedbackAsync(new ToolInteractionResponse
        {
            SessionId = requestedSessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new Netclaw.Actors.Protocol.ApprovalOptionKey(selectedKey),
            SenderId = identity.SenderId
        });
    }

    /// <summary>
    /// Cleans up session state when a SignalR connection disconnects.
    /// Shuts down the <see cref="SignalRSessionActor"/> so its subscriber is stopped,
    /// triggering <c>WatchWith</c> → <c>LeaveSession</c> on the LLM session actor.
    /// Removes the session from <c>_knownSessions</c> so that reconnection via
    /// <see cref="EnsureSessionAsync"/> takes the "unknown session" path and sends
    /// <see cref="StartSignalRSession"/> — required because a new child actor starts
    /// in <c>Initializing</c> and will only transition to <c>Active</c> on that message.
    /// </summary>
    public async Task OnDisconnectedAsync(string connectionId)
    {
        var parsed = ParseConnectionId(connectionId);

        await _sessionMutationGate.WaitAsync();
        try
        {
            if (_connections.TryGetSessionForConnection(parsed, out var sessionId))
            {
                _logger.LogDebug(
                    "Connection {ConnectionId} disconnected; shutting down SignalR session {SessionId}.",
                    connectionId, sessionId.Value);

                _connections.Disconnect(parsed);
                _knownSessions.Remove(sessionId);

                try
                {
                    var gateway = await _gatewayProvider.GetAsync();
                    gateway.Tell(new ShutdownSignalRSession(sessionId));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to send shutdown to SignalR session {SessionId}.",
                        sessionId.Value);
                }
            }
            else
            {
                _connections.Disconnect(parsed);
            }
        }
        finally
        {
            _sessionMutationGate.Release();
        }
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

    private static Actors.Channels.ChannelType ParseChannelType(string channelType)
    {
        if (ChannelTypeExtensions.TryFromWireValue(channelType, out var parsed))
            return parsed;

        throw new HubException($"Unknown channel type: '{channelType}'.");
    }

    private void ThrowIfIngressClosed()
    {
        var reason = _ingressGate.ClosedReason;
        if (reason is not null)
            throw new HubException(reason);
    }
}
