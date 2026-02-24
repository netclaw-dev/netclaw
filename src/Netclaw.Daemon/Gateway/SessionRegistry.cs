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
/// <see cref="SessionPipeline"/> (Akka.Streams) via
/// <see cref="IHubContext{THub,T}"/> for thread-safe output delivery.
/// </summary>
public sealed class SessionRegistry
{
    private readonly SessionPipeline _pipeline;
    private readonly ActorSystem _system;
    private readonly TimeProvider _timeProvider;
    private readonly IHubContext<SessionHub, ISessionHubClient> _hubContext;
    private readonly ILogger<SessionRegistry> _logger;

    // sessionId → session state
    private readonly ConcurrentDictionary<string, HubSession> _sessions = new();

    // connectionId → sessionId
    private readonly ConcurrentDictionary<string, string> _connections = new();

    public SessionRegistry(
        SessionPipeline pipeline,
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
        // Guard: one session per connection — dispose previous if caller retries
        if (_connections.TryRemove(connectionId, out var previousSessionId))
        {
            if (_sessions.TryRemove(previousSessionId, out var previous))
                await previous.Session.DisposeAsync();
        }

        var sessionId = new SessionId($"signalr/{Guid.NewGuid():N}");

        var session = await _pipeline.CreateAsync(sessionId, new SessionPipelineOptions
        {
            ChannelType = channelType
        });

        // Materialize output: stream → SignalR client.
        // ReceiveOutput returns Task; fire-and-forget is acceptable here because
        // SignalR's IHubContext handles disconnected clients gracefully (logs and
        // discards). We log exceptions rather than letting them go unobserved.
        session.Output
            .To(Sink.ForEach<SessionOutput>(output =>
            {
                var dto = SessionOutputMapper.ToDto(output);
                _hubContext.Clients.Client(connectionId).ReceiveOutput(dto)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            _logger.LogDebug(t.Exception,
                                "Failed to send output to connection {ConnectionId}", connectionId);
                    }, TaskContinuationOptions.OnlyOnFaulted);
            }))
            .Run(_system);

        // Materialize input: imperative queue → session sink
        var inputQueue = Source.Queue<ChannelInput>(16, OverflowStrategy.Backpressure)
            .ToMaterialized(session.Input, Keep.Left)
            .Run(_system);

        var hubSession = new HubSession(session, inputQueue, sessionId, connectionId);

        _sessions[sessionId.Value] = hubSession;
        _connections[connectionId] = sessionId.Value;

        return sessionId.Value;
    }

    /// <summary>
    /// Pushes a user message into an existing session's input queue.
    /// </summary>
    public async Task SendMessageAsync(string sessionId, string text)
    {
        if (!_sessions.TryGetValue(sessionId, out var hubSession))
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
    /// Disposes the materialized session (kills Akka.Streams pipelines).
    /// </summary>
    public async Task OnDisconnectedAsync(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var sessionId))
            return;

        if (_sessions.TryRemove(sessionId, out var hubSession))
            await hubSession.Session.DisposeAsync();
    }

    /// <summary>
    /// Internal state for a SignalR-connected session.
    /// </summary>
    private sealed record HubSession(
        MaterializedSession Session,
        ISourceQueueWithComplete<ChannelInput> InputQueue,
        SessionId SessionId,
        string ConnectionId);
}
