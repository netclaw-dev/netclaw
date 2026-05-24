// -----------------------------------------------------------------------
// <copyright file="FeedbackDrivenSessionPipeline.cs" company="Petabridge, LLC">
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

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class FeedbackDrivenSessionPipeline : ISessionPipeline
{
    private readonly IReadOnlyList<SessionOutput> _initialOutputs;
    private readonly Func<IWithSessionId, IReadOnlyList<SessionOutput>>? _feedbackOutputsFactory;
    private readonly Func<IWithSessionId, ICommandReply>? _replyFactory;
    private readonly ConcurrentBag<IWithSessionId> _recordedFeedback = [];
    private Channel<SessionOutput>? _outputs;

    public FeedbackDrivenSessionPipeline(
        IReadOnlyList<SessionOutput> initialOutputs,
        Func<IWithSessionId, IReadOnlyList<SessionOutput>>? feedbackOutputsFactory = null,
        Func<IWithSessionId, ICommandReply>? replyFactory = null)
    {
        _initialOutputs = initialOutputs;
        _feedbackOutputsFactory = feedbackOutputsFactory;
        _replyFactory = replyFactory;
    }

    public IReadOnlyCollection<IWithSessionId> RecordedFeedback => _recordedFeedback;

    public Task<MaterializedSession> CreateAsync(
        SessionId sessionId,
        SessionPipelineOptions options,
        IMaterializer? materializer = null,
        CancellationToken cancellationToken = default)
    {
        _outputs = Channel.CreateUnbounded<SessionOutput>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        foreach (var initialOutput in _initialOutputs)
            _outputs.Writer.TryWrite(initialOutput);

        var killSwitch = KillSwitches.Shared($"feedback-{sessionId.Value}");
        var input = Sink.ForEach<ChannelInput>(_ => { }).MapMaterializedValue(_ => NotUsed.Instance);
        var outputSource = Source.UnfoldAsync<int, SessionOutput>(0, async state =>
            {
                var next = await _outputs.Reader.ReadAsync(cancellationToken);
                return (state + 1, next);
            })
            .Via(killSwitch.Flow<SessionOutput>());

        return Task.FromResult(new MaterializedSession(input, outputSource, killSwitch));
    }

    public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
    {
        RecordFeedback(feedback);
        return Task.CompletedTask;
    }

    public Task<ICommandReply> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
    {
        RecordFeedback(feedback);
        return Task.FromResult(_replyFactory?.Invoke(feedback) ?? CommandAck.For(feedback.SessionId));
    }

    private void RecordFeedback(IWithSessionId feedback)
    {
        _recordedFeedback.Add(feedback);

        if (_outputs is null || _feedbackOutputsFactory is null)
            return;

        foreach (var output in _feedbackOutputsFactory(feedback))
            _outputs.Writer.TryWrite(output);
    }
}
