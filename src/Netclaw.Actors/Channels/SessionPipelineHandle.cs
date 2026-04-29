// -----------------------------------------------------------------------
// <copyright file="SessionPipelineHandle.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Manages the lifecycle of a materialized session pipeline on behalf of an
/// owning actor. Not thread-safe — designed for use within a single actor context
/// (no concurrent access). Supports two modes:
/// <list type="bullet">
///   <item>Long-lived (with reinitialization) for binding actors (Slack, SignalR)</item>
///   <item>Short-lived (fire-and-forget) for execution actors (Reminders, Webhooks)</item>
/// </list>
/// The handle does not own the <see cref="ActorMaterializer"/>. The owning actor
/// creates the materializer from its context and passes it in; Akka disposes it
/// automatically when the actor stops.
/// </summary>
public sealed class SessionPipelineHandle
{
    private readonly ISessionPipeline _pipeline;
    private readonly ILoggingAdapter _log;
    private readonly string _materializerNamePrefix;

    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    private MaterializedSession? _session;
    private Task? _outputCompletion;
    private ChannelWriter<ChannelInput>? _inputQueue;
    private int _pipelineGeneration;
    private bool _isReinitializing;

    // Stored from first InitializeWithChannelAsync for reinit
    private IActorContext? _storedContext;
    private SessionId? _storedSessionId;
    private SessionPipelineOptions? _storedOptions;
    private Action<SessionOutput>? _storedOnOutput;
    private Action<int, Exception?>? _storedOnStreamTerminated;

    public SessionPipelineHandle(
        ISessionPipeline pipeline,
        ILoggingAdapter log,
        string materializerNamePrefix)
    {
        _pipeline = pipeline;
        _log = log;
        _materializerNamePrefix = materializerNamePrefix;
    }

    /// <summary>The current pipeline generation, for <c>OutputStreamTerminated</c> filtering.</summary>
    public int Generation => _pipelineGeneration;

    /// <summary>The <see cref="System.Threading.Channels.ChannelWriter{T}"/> for long-lived actors to write input.
    /// Null if not initialized or if initialized via queue mode.</summary>
    public ChannelWriter<ChannelInput>? InputQueue => _inputQueue;

    /// <summary>Whether the handle has been initialized (session is not null).</summary>
    public bool IsInitialized => _session is not null;

    /// <summary>
    /// Idempotent pipeline creation for long-lived actors that use <see cref="Source.Channel{T}(int, bool)"/>
    /// for ongoing input. Stores all parameters for use by <see cref="ReinitializeAsync"/>.
    /// </summary>
    public async Task<ChannelWriter<ChannelInput>> InitializeWithChannelAsync(
        IActorContext context,
        SessionId sessionId,
        SessionPipelineOptions options,
        Action<SessionOutput> onOutput,
        Action<int, Exception?> onStreamTerminated,
        CancellationToken cancellationToken = default)
    {
        if (_session is not null)
            return _inputQueue!;

        // Store for reinit
        _storedContext = context;
        _storedSessionId = sessionId;
        _storedOptions = options;
        _storedOnOutput = onOutput;
        _storedOnStreamTerminated = onStreamTerminated;

        _log.Info("Initializing {0} session pipeline", _materializerNamePrefix);

        var materializer = context.Materializer(namePrefix: _materializerNamePrefix);

        var materialized = await _pipeline.CreateAsync(
            sessionId, options,
            materializer: materializer,
            cancellationToken: cancellationToken);

        var inputQueue = Source.Channel<ChannelInput>(512, true)
            .ToMaterialized(materialized.Input, Keep.Left)
            .Run(materializer);

        var generation = ++_pipelineGeneration;
        var outputTerminated = materialized.Output
            .WatchTermination((_, done) => done)
            .ToMaterialized(
                Sink.ForEach<SessionOutput>(onOutput),
                Keep.Left)
            .Run(materializer);

        _outputCompletion = outputTerminated;

        _ = ObserveTerminationAsync();

        _session = materialized;

        async Task ObserveTerminationAsync()
        {
            try
            {
                await outputTerminated;
                onStreamTerminated(generation, null);
            }
            catch (Exception ex)
            {
                onStreamTerminated(generation, ex);
            }
        }
        _inputQueue = inputQueue;

        _log.Info("{0} session pipeline initialized", _materializerNamePrefix);
        return inputQueue;
    }

    /// <summary>
    /// Pipeline creation for fire-and-forget execution actors that use
    /// <see cref="Source.Queue{T}(int,OverflowStrategy)"/>, offer input once, and complete.
    /// Does not wire stream-terminated detection (the actor stops on <c>TurnCompleted</c>/<c>ErrorOutput</c>).
    /// </summary>
    public async Task<ISourceQueueWithComplete<ChannelInput>> InitializeWithQueueAsync(
        IActorContext context,
        SessionId sessionId,
        SessionPipelineOptions options,
        Action<SessionOutput> onOutput,
        CancellationToken cancellationToken = default)
    {
        _log.Info("Initializing {0} execution pipeline", _materializerNamePrefix);

        var materializer = context.Materializer(namePrefix: _materializerNamePrefix);

        var materialized = await _pipeline.CreateAsync(
            sessionId, options,
            materializer: materializer,
            cancellationToken: cancellationToken);

        var inputQueue = Source.Queue<ChannelInput>(8, OverflowStrategy.Backpressure)
            .ToMaterialized(materialized.Input, Keep.Left)
            .Run(materializer);

        _outputCompletion = materialized.Output
            .WatchTermination((_, done) => done)
            .ToMaterialized(
                Sink.ForEach<SessionOutput>(onOutput),
                Keep.Left)
            .Run(materializer);

        _session = materialized;

        _log.Info("{0} execution pipeline initialized", _materializerNamePrefix);
        return inputQueue;
    }

    /// <summary>
    /// Tears down the current pipeline and re-initializes using the parameters
    /// stored from the original <see cref="InitializeWithChannelAsync"/> call.
    /// Guards against concurrent reinitialization. On failure, invokes
    /// <paramref name="onReinitFailed"/>.
    /// </summary>
    public async Task ReinitializeAsync(string reason, Action onReinitFailed)
    {
        if (_isReinitializing)
            return;

        if (_storedContext is null || _storedSessionId is null || _storedOptions is null
            || _storedOnOutput is null || _storedOnStreamTerminated is null)
        {
            _log.Warning("Cannot reinitialize {0} pipeline: not yet initialized via channel mode", _materializerNamePrefix);
            return;
        }

        _isReinitializing = true;
        try
        {
            _log.Warning("Reinitializing {0} session pipeline: {1}", _materializerNamePrefix, reason);

            await DrainAsync();

            await InitializeWithChannelAsync(
                _storedContext, _storedSessionId.Value, _storedOptions,
                _storedOnOutput, _storedOnStreamTerminated);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{0} pipeline reinitialization failed; scheduling retry", _materializerNamePrefix);
            onReinitFailed();
        }
        finally
        {
            _isReinitializing = false;
        }
    }

    /// <summary>
    /// Shuts down the kill switch and waits for the output stream to finish
    /// draining. Must be called <b>before</b> <c>Context.Stop(Self)</c> so
    /// that stream stage actors (children of the materializer's actor context)
    /// complete gracefully rather than being abruptly terminated when the
    /// parent actor stops.
    /// </summary>
    public async Task DrainAsync()
    {
        _inputQueue?.TryComplete();
        _inputQueue = null;

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        if (_outputCompletion is not null)
        {
            try
            {
                await _outputCompletion.WaitAsync(StreamDrainTimeout);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "{0} output stream faulted or timed out during drain", _materializerNamePrefix);
            }

            _outputCompletion = null;
        }
    }

    /// <summary>
    /// Best-effort cleanup for actor <c>PostStop</c>. Completes the input
    /// queue if it wasn't already drained. Does not dispose the materializer —
    /// the actor's context owns that lifecycle.
    /// </summary>
    public void Dispose()
    {
        _inputQueue?.TryComplete();
    }

}
