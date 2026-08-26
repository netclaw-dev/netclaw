// -----------------------------------------------------------------------
// <copyright file="RecordingSessionPipeline.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Threading.Channels;
using Akka;
using Akka.Streams;
using Akka.Streams.Dsl;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

public sealed class RecordingSessionPipeline : ISessionPipeline
{
    private readonly object _feedbackLock = new();
    private readonly List<IWithSessionId> _recordedFeedback = [];
    private readonly Func<SessionId, IReadOnlyList<SessionOutput>> _outputFactory;
    private readonly bool _reactive;
    private readonly TaskCompletionSource<SessionPipelineOptions> _created = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private SessionPipelineOptions? _capturedOptions;
    private SharedKillSwitch? _killSwitch;

    /// <summary>
    /// Creates a recording pipeline.
    /// </summary>
    /// <param name="outputFactory">Factory that produces the output for a given session.</param>
    /// <param name="reactive">
    /// When <c>true</c>, the output stream waits for the first input to arrive
    /// before emitting any outputs. This models real pipeline behavior where the
    /// LLM only produces output in response to input, and is required for tests
    /// that depend on the actor processing the inbound message (and setting
    /// <c>_pendingCursorSnowflake</c>) before <c>TurnCompleted</c> is delivered.
    /// <para>
    /// When <c>false</c> (default), outputs are emitted immediately on stream
    /// materialization — suitable for tests that don't send an inbound message
    /// and rely on output appearing as soon as the pipeline initializes.
    /// </para>
    /// </param>
    public RecordingSessionPipeline(
        Func<SessionId, IReadOnlyList<SessionOutput>> outputFactory,
        bool reactive = false)
    {
        _outputFactory = outputFactory;
        _reactive = reactive;
    }

    public SessionPipelineOptions? CapturedOptions => Volatile.Read(ref _capturedOptions);
    public Task<SessionPipelineOptions> Created => _created.Task;
    public IReadOnlyList<IWithSessionId> RecordedFeedback
    {
        get { lock (_feedbackLock) return _recordedFeedback.ToList(); }
    }

    public ConcurrentQueue<ChannelInput> CapturedInputs { get; } = new();
    public Func<IWithSessionId, CancellationToken, Task<ISessionResponse>>? ResponseFactory { get; set; }

    /// <summary>
    /// Number of <see cref="CreateAsync"/> calls. A supervised actor restart
    /// re-creates the pipeline, so tests observe a restart as a second call.
    /// </summary>
    public int CreateCount => Volatile.Read(ref _createCount);
    private int _createCount;

    /// <summary>
    /// When set, <see cref="SendFeedbackAsync"/> throws this exception and
    /// does not record the feedback. This models a dead session feedback pipe.
    /// </summary>
    public Exception? FeedbackException { get; set; }

    /// <summary>
    /// Completes the output stream of the most recent <see cref="CreateAsync"/>
    /// call. The binding actor observes the completion as
    /// <c>OutputStreamTerminated</c> and answers it with a pipeline
    /// reinitialize, so a test drives the reinitialize path without a
    /// channel-private message type.
    /// </summary>
    public void TerminateOutputStream()
    {
        var killSwitch = Volatile.Read(ref _killSwitch)
            ?? throw new InvalidOperationException("The pipeline is not created yet.");
        killSwitch.Shutdown();
    }

    public Task<MaterializedSession> CreateAsync(
        SessionId sessionId,
        SessionPipelineOptions options,
        IMaterializer? materializer = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _createCount);
        Volatile.Write(ref _capturedOptions, options);
        _created.TrySetResult(options);

        var killSwitch = KillSwitches.Shared($"recording-{sessionId.Value}");
        Volatile.Write(ref _killSwitch, killSwitch);
        var outputs = _outputFactory(sessionId).ToList();

        Source<SessionOutput, NotUsed> output;
        Sink<ChannelInput, NotUsed> input;

        if (_reactive)
        {
            // Gate output emission on first input arrival. The channel bridges
            // the input sink (Akka.Streams) to the output source so that the
            // actor processes HandleInboundAsync (setting _pendingCursorSnowflake)
            // before OutputReceived(TurnCompleted) arrives.
            var gate = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true
            });
            var gateOpened = false;

            input = Sink.ForEach<ChannelInput>(ci =>
                {
                    CapturedInputs.Enqueue(ci);
                    if (!gateOpened)
                    {
                        gateOpened = true;
                        gate.Writer.TryWrite(true);
                    }
                })
                .ObservingFault();

            // Wait for gate signal, then emit all outputs.
            output = Source.UnfoldAsync<int, SessionOutput>(0, async state =>
                {
                    if (state == 0)
                    {
                        // Wait for the first input to arrive before emitting anything.
                        await gate.Reader.ReadAsync(cancellationToken);
                    }

                    if (state < outputs.Count)
                        return (state + 1, outputs[state]);

                    // All outputs emitted; keep stream alive.
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return default; // unreachable
                })
                .Via(killSwitch.Flow<SessionOutput>());
        }
        else
        {
            input = Sink.ForEach<ChannelInput>(ci => CapturedInputs.Enqueue(ci))
                .ObservingFault();

            output = Source.From(outputs)
                .Concat(Source.Never<SessionOutput>())
                .Via(killSwitch.Flow<SessionOutput>());
        }

        return Task.FromResult(new MaterializedSession(input, output, killSwitch));
    }

    public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
    {
        if (FeedbackException is { } feedbackException)
            throw feedbackException;
        lock (_feedbackLock) _recordedFeedback.Add(feedback);
        return Task.CompletedTask;
    }

    public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
    {
        lock (_feedbackLock) _recordedFeedback.Add(feedback);
        var response = ResponseFactory?.Invoke(feedback, ct)
            ?? Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
        return response;
    }
}
