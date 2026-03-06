using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Singleton service managing the lifecycle of SignalR-connected sessions.
/// Bridges <see cref="SessionHub"/> (transient per-invocation) to
/// <see cref="ISessionPipeline"/> (Akka.Streams) via
/// <see cref="IHubContext{THub,T}"/> for thread-safe output delivery.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ISessionPipeline _pipeline;
    private readonly ActorSystem _system;
    private readonly TimeProvider _timeProvider;
    private readonly IHubContext<SessionHub, ISessionHubClient> _hubContext;
    private readonly ILogger<SessionRegistry> _logger;

    // sessionId → session state
    private readonly ConcurrentDictionary<SessionId, HubSessionState> _sessions = new();
    private readonly SemaphoreSlim _sessionMutationGate = new(1, 1);

    private readonly SessionConnectionMap _connections = new();

    public SessionRegistry(
        ISessionPipeline pipeline,
        ActorSystem system,
        TimeProvider timeProvider,
        IHubContext<SessionHub, ISessionHubClient> hubContext,
        ILogger<SessionRegistry> logger)
    {
        _pipeline = pipeline;
        _system = system;
        _timeProvider = timeProvider;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new session for the given SignalR connection.
    /// Materializes Akka.Streams pipelines for input (queue) and output
    /// (routed back to the caller via <see cref="IHubContext{THub,T}"/>).
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
        await MaterializeAndBindSessionAsync(sessionId, callerConnectionId, channelType);
        return sessionId.Value;
    }

    private async Task MaterializeAndBindSessionAsync(
        SessionId sessionId,
        SignalRConnectionId callerConnectionId,
        string channelType)
    {
        var existing = _sessions.TryGetValue(sessionId, out var existingSession)
            ? existingSession
            : null;

        var session = await _pipeline.CreateAsync(sessionId, new SessionPipelineOptions
        {
            ChannelType = channelType
        });

        // Materialize input: imperative queue → session sink
        var inputQueue = Source.Queue<ChannelInput>(16, OverflowStrategy.Backpressure)
            .ToMaterialized(session.Input, Keep.Left)
            .Run(_system);

        // Materialize output: stream → currently attached SignalR connection.
        // Track completion to detect post-passivation stream death.
        var outputCompletion = session.Output
            .ToMaterialized(
                Sink.ForEach<SessionOutput>(output => PublishOutput(sessionId, output)),
                Keep.Right)
            .Run(_system);

        var hubSession = new HubSessionState(session, inputQueue, channelType, outputCompletion);

        _sessions[sessionId] = hubSession;

        // Guard: one session per connection — dispose previous if caller retries.
        var previousSessionId = _connections.BindNewSession(sessionId, callerConnectionId);
        if (previousSessionId.HasValue && _sessions.TryRemove(previousSessionId.Value, out var previous))
        {
            await previous.Session.DisposeAsync();
        }

        if (existing is not null)
            await existing.Session.DisposeAsync();

        _logger.LogDebug(
            "Session {SessionId} pipeline materialized and bound to connection {ConnectionId}.",
            sessionId.Value, callerConnectionId.Value);
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
                if (_sessions.TryGetValue(requestedSessionId, out var hubSession))
                {
                    if (hubSession.IsOutputCompleted)
                    {
                        _logger.LogWarning(
                            "Session {SessionId} output stream has completed (post-passivation); " +
                            "re-materializing pipeline for connection {ConnectionId}.",
                            requestedSessionId.Value, callerConnectionId.Value);

                        await MaterializeAndBindSessionAsync(
                            requestedSessionId, callerConnectionId, hubSession.ChannelType);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Attaching connection {ConnectionId} to existing session {SessionId}.",
                            callerConnectionId.Value, requestedSessionId.Value);

                        _connections.AttachSession(requestedSessionId, callerConnectionId);
                    }

                    return new SessionEnsureResultDto
                    {
                        SessionId = requestedSessionId.Value,
                        Created = false
                    };
                }

                await MaterializeAndBindSessionAsync(requestedSessionId, callerConnectionId, channelType);
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
    /// If the session's output stream has completed (post-passivation), the pipeline
    /// is re-materialized so output delivery resumes.
    /// </summary>
    public async Task AttachSessionAsync(string connectionId, string sessionId)
    {
        var callerConnectionId = ParseConnectionId(connectionId);
        var requestedSessionId = ParseSessionId(sessionId);

        await _sessionMutationGate.WaitAsync();
        try
        {
            if (!_sessions.TryGetValue(requestedSessionId, out var hubSession))
                throw new HubException($"Session '{sessionId}' not found.");

            if (hubSession.IsOutputCompleted)
            {
                _logger.LogWarning(
                    "Session {SessionId} output stream has completed (post-passivation); " +
                    "re-materializing pipeline for connection {ConnectionId}.",
                    requestedSessionId.Value, callerConnectionId.Value);

                await MaterializeAndBindSessionAsync(
                    requestedSessionId, callerConnectionId, hubSession.ChannelType);
            }
            else
            {
                _logger.LogDebug(
                    "Attaching connection {ConnectionId} to session {SessionId}.",
                    callerConnectionId.Value, requestedSessionId.Value);

                _connections.AttachSession(requestedSessionId, callerConnectionId);
            }
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
        {
            throw new HubException($"Session '{sessionId}' is not attached to this connection.");
        }

        if (!attachedSessionId.Equals(requestedSessionId))
        {
            throw new HubException($"Session '{sessionId}' is not attached to this connection.");
        }

        if (!_sessions.TryGetValue(attachedSessionId, out var hubSession))
            throw new HubException($"Session '{sessionId}' not found.");

        var result = await hubSession.InputQueue.OfferAsync(new ChannelInput
        {
            SenderId = "signalr-user",
            Contents = [new TextContent(text)],
            ReceivedAt = _timeProvider.GetUtcNow()
        });

        if (result is QueueOfferResult.Failure failure)
            throw new HubException($"Failed to enqueue message: {failure.Cause.Message}");

        if (result is QueueOfferResult.QueueClosed)
            throw new HubException($"Session '{sessionId}' is closed.");
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
            var sessions = _sessions.ToArray();
            _sessions.Clear();
            _connections.Clear();

            var disposeTasks = sessions
                .Select(x => x.Value.Session.DisposeAsync().AsTask())
                .ToArray();

            if (disposeTasks.Length == 0)
                return;

            try
            {
                await Task.WhenAll(disposeTasks).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timed out while disposing {Count} active session(s) during shutdown.", disposeTasks.Length);
            }
        }
        finally
        {
            _sessionMutationGate.Release();
        }
    }

    /// <summary>
    /// Returns the output completion task for the given session.
    /// FOR TESTING ONLY — used to await stream completion in integration tests.
    /// </summary>
    internal Task? GetOutputCompletionForTesting(SessionId sessionId)
        => _sessions.TryGetValue(sessionId, out var s) ? s.OutputCompletion : null;

    private void PublishOutput(SessionId sessionId, SessionOutput output)
    {
        if (!_sessions.ContainsKey(sessionId))
            return;

        if (!_connections.TryGetConnectionForSession(sessionId, out var connectionId))
        {
            _logger.LogDebug(
                "Session {SessionId} has no active connection binding; dropping {OutputType} output.",
                sessionId.Value, output.GetType().Name);
            return;
        }

        var dto = SessionOutputDtoMapper.ToDto(output);
        _hubContext.Clients.Client(connectionId.Value).ReceiveOutput(dto)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.LogDebug(t.Exception,
                        "Failed to send output to connection {ConnectionId}", connectionId);
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
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

    /// <summary>
    /// Internal state for a SignalR-connected session.
    /// </summary>
    private sealed class HubSessionState
    {
        public HubSessionState(
            MaterializedSession session,
            ISourceQueueWithComplete<ChannelInput> inputQueue,
            string channelType,
            Task outputCompletion)
        {
            Session = session;
            InputQueue = inputQueue;
            ChannelType = channelType;
            OutputCompletion = outputCompletion;
        }

        public MaterializedSession Session { get; }
        public ISourceQueueWithComplete<ChannelInput> InputQueue { get; }

        /// <summary>Channel type used when creating the pipeline (needed for re-materialization).</summary>
        public string ChannelType { get; }

        /// <summary>
        /// Completes when the Akka.Streams output pipeline terminates.
        /// Used to detect post-passivation stream death before deciding whether to re-materialize.
        /// </summary>
        public Task OutputCompletion { get; }

        /// <summary>True when the output stream has terminated (e.g. after actor passivation).</summary>
        public bool IsOutputCompleted => OutputCompletion.IsCompleted;
    }
}
