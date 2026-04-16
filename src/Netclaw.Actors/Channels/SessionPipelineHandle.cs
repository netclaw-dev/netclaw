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
/// </summary>
public sealed class SessionPipelineHandle : IAsyncDisposable
{
    private readonly ISessionPipeline _pipeline;
    private readonly ILoggingAdapter _log;
    private readonly string _materializerNamePrefix;

    private ActorMaterializer? _materializer;
    private MaterializedSession? _session;
    private ChannelWriter<ChannelInput>? _inputQueue;
    private int _pipelineGeneration;
    private bool _isReinitializing;

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
    /// for ongoing input. Returns the <see cref="ChannelWriter{T}"/> for the caller to write input.
    /// Wires output stream termination detection via the <paramref name="onStreamTerminated"/> callback.
    /// </summary>
    /// <param name="context">The owning actor's context (used to scope the materializer).</param>
    /// <param name="sessionId">Session identity.</param>
    /// <param name="options">Channel-specific pipeline options.</param>
    /// <param name="onOutput">Callback invoked for each <see cref="SessionOutput"/> (typically <c>output => self.Tell(new MyWrapper(output))</c>).</param>
    /// <param name="onStreamTerminated">Callback with <c>(generation, cause)</c> when the output stream terminates.</param>
    /// <param name="cancellationToken">Cancellation token for pipeline creation.</param>
    /// <returns>The <see cref="ChannelWriter{T}"/> for writing input.</returns>
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

        _log.Info("Initializing {0} session pipeline", _materializerNamePrefix);

        _materializer = context.Materializer(namePrefix: _materializerNamePrefix);

        var materialized = await _pipeline.CreateAsync(
            sessionId, options,
            materializer: _materializer,
            cancellationToken: cancellationToken);

        var inputQueue = Source.Channel<ChannelInput>(512, true)
            .ToMaterialized(materialized.Input, Keep.Left)
            .Run(_materializer);

        var generation = ++_pipelineGeneration;
        var outputCompletion = materialized.Output
            .ToMaterialized(
                Sink.ForEach<SessionOutput>(onOutput),
                Keep.Right)
            .Run(_materializer);

        _ = outputCompletion.ContinueWith(t =>
            {
                var cause = t.IsFaulted ? t.Exception?.GetBaseException() : null;
                onStreamTerminated(generation, cause);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _session = materialized;
        _inputQueue = inputQueue;

        _log.Info("{0} session pipeline initialized", _materializerNamePrefix);
        return inputQueue;
    }

    /// <summary>
    /// Pipeline creation for fire-and-forget execution actors that use
    /// <see cref="Source.Queue{T}(int,OverflowStrategy)"/>, offer input once, and complete.
    /// Does not wire stream-terminated detection (the actor stops on <c>TurnCompleted</c>/<c>ErrorOutput</c>).
    /// </summary>
    /// <param name="context">The owning actor's context (used to scope the materializer).</param>
    /// <param name="sessionId">Session identity.</param>
    /// <param name="options">Channel-specific pipeline options.</param>
    /// <param name="onOutput">Callback invoked for each <see cref="SessionOutput"/>.</param>
    /// <param name="cancellationToken">Cancellation token for pipeline creation.</param>
    /// <returns>The <see cref="ISourceQueueWithComplete{T}"/> for offering input and completing.</returns>
    public async Task<ISourceQueueWithComplete<ChannelInput>> InitializeWithQueueAsync(
        IActorContext context,
        SessionId sessionId,
        SessionPipelineOptions options,
        Action<SessionOutput> onOutput,
        CancellationToken cancellationToken = default)
    {
        _log.Info("Initializing {0} execution pipeline", _materializerNamePrefix);

        _materializer = context.Materializer(namePrefix: _materializerNamePrefix);

        var materialized = await _pipeline.CreateAsync(
            sessionId, options,
            materializer: _materializer,
            cancellationToken: cancellationToken);

        var inputQueue = Source.Queue<ChannelInput>(8, OverflowStrategy.Backpressure)
            .ToMaterialized(materialized.Input, Keep.Left)
            .Run(_materializer);

        materialized.Output
            .To(Sink.ForEach<SessionOutput>(onOutput))
            .Run(_materializer);

        _session = materialized;

        _log.Info("{0} execution pipeline initialized", _materializerNamePrefix);
        return inputQueue;
    }

    /// <summary>
    /// Tears down the current pipeline and re-initializes. Guards against concurrent
    /// reinitialization. On failure, invokes <paramref name="onReinitFailed"/>.
    /// Only used by long-lived binding actors.
    /// </summary>
    public async Task ReinitializeAsync(
        string reason,
        IActorContext context,
        SessionId sessionId,
        SessionPipelineOptions options,
        Action<SessionOutput> onOutput,
        Action<int, Exception?> onStreamTerminated,
        Action onReinitFailed)
    {
        if (_isReinitializing)
            return;

        _isReinitializing = true;
        try
        {
            _log.Warning("Reinitializing {0} session pipeline: {1}", _materializerNamePrefix, reason);

            _inputQueue?.TryComplete();
            _inputQueue = null;

            if (_session is not null)
            {
                await _session.DisposeAsync();
                _session = null;
            }

            _materializer?.Dispose();
            _materializer = null;

            await InitializeWithChannelAsync(context, sessionId, options, onOutput, onStreamTerminated);
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
    /// Synchronous cleanup for actor <c>PostStop</c>. Completes input queue,
    /// disposes session and materializer.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _inputQueue?.TryComplete();

        try
        {
            _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to dispose {0} session during cleanup", _materializerNamePrefix);
        }

        _materializer?.Dispose();

        return ValueTask.CompletedTask;
    }
}
