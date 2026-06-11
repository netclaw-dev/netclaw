// -----------------------------------------------------------------------
// <copyright file="RoutingChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Invokes the composed pipelines chosen by an <see cref="IChatClientRouter"/>,
/// walking the ordered candidate list with failover semantics. This single walk
/// generalizes the previous <c>FailoverChatClient</c> (a two-candidate list) and
/// <c>AlertingChatClientDecorator</c> (a one-candidate list whose terminal failure
/// raises <c>provider.unreachable</c>).
///
/// Streaming fails over only <b>before the first chunk is yielded</b> from a candidate
/// — once any update has been emitted, a later failure propagates so already-streamed
/// output is never duplicated by switching providers (the same invariant the streaming
/// retry decorator below it enforces per provider).
/// </summary>
public sealed class RoutingChatClient : IChatClient
{
    private readonly IChatClientRouter _router;
    private readonly ChatRoutingContext _context;
    private readonly IOperationalNotificationSink _sink;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public RoutingChatClient(
        IChatClientRouter router,
        ChatRoutingContext context,
        IOperationalNotificationSink sink,
        ILogger logger,
        TimeProvider timeProvider)
    {
        _router = router;
        _context = context;
        _sink = sink;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var candidates = _router.Route(_context);

        for (var i = 0; i < candidates.Count; i++)
        {
            var isLast = i == candidates.Count - 1;
            try
            {
                return await candidates[i].GetResponseAsync(messageList, options, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !isLast)
            {
                EmitFailover(ex);
                LogWithSession(LogLevel.Warning, ex, "LLM provider failed, failing over to next candidate");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                EmitUnreachable(ex, candidates.Count);
                LogWithSession(LogLevel.Error, ex, UnreachableMessage(candidates.Count));
                throw;
            }
        }

        throw new InvalidOperationException("Router returned no chat-client candidates.");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var candidates = _router.Route(_context);
        if (candidates.Count == 0)
            throw new InvalidOperationException("Router returned no chat-client candidates.");

        for (var i = 0; i < candidates.Count; i++)
        {
            var isLast = i == candidates.Count - 1;
            var yieldedChunk = false;
            Exception? failure = null;

            // Initiation can throw before any enumerator is produced.
            IAsyncEnumerable<ChatResponseUpdate>? stream = null;
            try
            {
                stream = candidates[i].GetStreamingResponseAsync(messageList, options, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failure = ex;
            }

            if (stream is not null)
            {
                await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    ChatResponseUpdate update;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                            break;

                        update = enumerator.Current;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !yieldedChunk)
                    {
                        // Pre-first-chunk failure: safe to fail over (nothing emitted yet).
                        failure = ex;
                        break;
                    }

                    // Past here a chunk has been emitted; a later failure is NOT caught
                    // above (yieldedChunk is true) and propagates to the consumer.
                    yieldedChunk = true;
                    yield return update;
                }
            }

            if (failure is null)
                yield break; // candidate completed (or a post-first-chunk throw already unwound)

            if (isLast)
            {
                EmitUnreachable(failure, candidates.Count);
                LogWithSession(LogLevel.Error, failure, UnreachableMessage(candidates.Count));
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            EmitFailover(failure);
            LogWithSession(LogLevel.Warning, failure, "LLM provider failed, failing over to next candidate");
            // outer loop advances to the next candidate
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        var candidates = _router.Route(_context);
        return candidates.Count > 0 ? candidates[0].GetService(serviceType, serviceKey) : null;
    }

    // Candidate pipelines are process-lifetime singletons owned by the router and may
    // be shared across roles; disposal is not this wrapper's responsibility.
    public void Dispose() { }

    private static string UnreachableMessage(int candidateCount) =>
        candidateCount == 1
            ? "LLM provider unreachable — no fallback configured"
            : "All LLM providers failed — every candidate unreachable";

    private void EmitFailover(Exception ex) =>
        _sink.Emit(OperationalAlert.Create(
            _timeProvider,
            "provider.failover",
            AlertType.ProviderFailover,
            "Primary LLM provider failed, failing over to fallback",
            AlertSeverity.Warning,
            context: new Dictionary<string, string> { ["error"] = ex.Message }));

    private void EmitUnreachable(Exception ex, int candidateCount) =>
        _sink.Emit(OperationalAlert.Create(
            _timeProvider,
            "provider.unreachable",
            AlertType.ProviderUnreachable,
            UnreachableMessage(candidateCount),
            AlertSeverity.Critical,
            context: new Dictionary<string, string> { ["error"] = ex.Message }));

    // Failover/outage events are logged here, outside any per-pipeline LoggingChatClient
    // scope, so attach the session id so they correlate by session in Seq.
    private void LogWithSession(LogLevel level, Exception ex, string message)
    {
        using (SessionLoggingScope.Begin(_logger))
            _logger.Log(level, ex, message);
    }
}

/// <summary>
/// <see cref="IChatClientProvider"/> backed by an <see cref="IChatClientRouter"/>.
/// Returns a <see cref="RoutingChatClient"/> per <see cref="ModelRole"/> (cached so a
/// role keeps a stable instance); candidate selection and failover happen per call
/// inside the routing client, so per-call routing is preserved.
/// </summary>
public sealed class RoutingChatClientProvider : IChatClientProvider
{
    private readonly IChatClientRouter _router;
    private readonly IOperationalNotificationSink _sink;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<ModelRole, IChatClient> _cache = new();

    public RoutingChatClientProvider(
        IChatClientRouter router,
        IOperationalNotificationSink sink,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _router = router;
        _sink = sink;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IChatClient GetClient(ModelRole role)
    {
        // Main and Fallback share one routing client (the main candidate list already
        // contains the fallback), preserving the prior provider's identity contract.
        var key = role == ModelRole.Fallback ? ModelRole.Main : role;
        return _cache.GetOrAdd(key, r => new RoutingChatClient(
            _router,
            new ChatRoutingContext { Role = r },
            _sink,
            _loggerFactory.CreateLogger<RoutingChatClient>(),
            _timeProvider));
    }
}
