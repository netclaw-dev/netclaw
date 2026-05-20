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

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

public sealed class RecordingSessionPipeline : ISessionPipeline
{
    private readonly Func<SessionId, IReadOnlyList<SessionOutput>> _outputFactory;
    private readonly bool _reactive;

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

    public SessionPipelineOptions? CapturedOptions { get; private set; }
    public List<IWithSessionId> RecordedFeedback { get; } = [];
    public ConcurrentQueue<ChannelInput> CapturedInputs { get; } = new();

    public Task<MaterializedSession> CreateAsync(
        SessionId sessionId,
        SessionPipelineOptions options,
        IMaterializer? materializer = null,
        CancellationToken cancellationToken = default)
    {
        CapturedOptions = options;

        var killSwitch = KillSwitches.Shared($"recording-{sessionId.Value}");
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
        RecordedFeedback.Add(feedback);
        return Task.CompletedTask;
    }

    public Task<ICommandReply> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
    {
        RecordedFeedback.Add(feedback);
        return Task.FromResult<ICommandReply>(CommandAck.For(feedback.SessionId));
    }
}
