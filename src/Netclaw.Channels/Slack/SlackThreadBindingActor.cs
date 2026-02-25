using System.Text;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Channels.Slack;

internal sealed class SlackThreadBindingActor : ReceiveActor
{
    private readonly SessionId _sessionId;
    private readonly string _channelId;
    private readonly string _threadTs;
    private readonly SlackGatewayDependencies _dependencies;
    private readonly ILoggingAdapter _log;

    private readonly StringBuilder _buffer = new();
    private bool _sawTextDelta;
    private bool _postedThisTurn;

    private MaterializedSession? _session;
    private ISourceQueueWithComplete<ChannelInput>? _inputQueue;

    public SlackThreadBindingActor(
        SessionId sessionId,
        string channelId,
        string threadTs,
        SlackGatewayDependencies dependencies)
    {
        _sessionId = sessionId;
        _channelId = channelId;
        _threadTs = threadTs;
        _dependencies = dependencies;
        _log = Context.GetLogger()
            .WithContext("Adapter", "slack")
            .WithContext("SessionId", _sessionId.Value)
            .WithContext("SlackChannelId", _channelId)
            .WithContext("SlackThreadTs", _threadTs);

        ReceiveAsync<SlackThreadInbound>(HandleInboundAsync);
        ReceiveAsync<ThreadOutput>(HandleOutputAsync);
    }

    public static Props CreateProps(
        SessionId sessionId,
        string channelId,
        string threadTs,
        SlackGatewayDependencies dependencies) =>
        Props.Create(() => new SlackThreadBindingActor(sessionId, channelId, threadTs, dependencies));

    protected override void PostStop()
    {
        _inputQueue?.Complete();
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.PostStop();
    }

    private async Task HandleInboundAsync(SlackThreadInbound message)
    {
        await EnsureInitializedAsync();

        var result = await _inputQueue!.OfferAsync(new ChannelInput
        {
            SenderId = message.SenderId,
            ChannelId = _channelId,
            Contents = [new TextContent(message.Text)],
            ReceivedAt = message.ReceivedAt
        });

        if (result is QueueOfferResult.QueueClosed)
        {
            _log.Warning("Slack thread queue closed for session {0}", _sessionId.Value);
            Context.Stop(Self);
            return;
        }

        if (result is QueueOfferResult.Enqueued)
        {
            _log.Debug("Accepted inbound Slack message for session queue");
            ChannelTelemetry.RecordSlackMessageEnqueued();
        }

        if (result is QueueOfferResult.Failure failure)
            _log.Error(failure.Cause, "Failed to enqueue Slack message for session {0}", _sessionId.Value);
    }

    private async Task EnsureInitializedAsync()
    {
        if (_session is not null)
            return;

        _log.Info("Initializing Slack thread binding pipeline");
        var self = Self;

        var materialized = await _dependencies.Pipeline.CreateAsync(_sessionId, new SessionPipelineOptions
        {
            ChannelType = "slack",
            Filter = OutputFilter.Full
        });

        var inputQueue = Source.Queue<ChannelInput>(32, OverflowStrategy.Backpressure)
            .ToMaterialized(materialized.Input, Keep.Left)
            .Run(_dependencies.ActorSystem);

        materialized.Output
            .To(Sink.ForEach<SessionOutput>(output => self.Tell(new ThreadOutput(output))))
            .Run(_dependencies.ActorSystem);

        _session = materialized;
        _inputQueue = inputQueue;

        _log.Info("Slack thread binding pipeline initialized");
    }

    private async Task HandleOutputAsync(ThreadOutput threadOutput)
    {
        switch (threadOutput.Output)
        {
            case TextDeltaOutput delta:
                _buffer.Append(delta.Delta);
                _sawTextDelta = true;
                break;

            case TextOutput text:
                if (!_sawTextDelta && !_postedThisTurn)
                {
                    var fullText = text.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(fullText))
                    {
                        await SafePostAsync(fullText);
                        _postedThisTurn = true;
                    }
                }

                break;

            case ErrorOutput err:
                await SafePostAsync($":warning: {err.Message}");
                _buffer.Clear();
                break;

            case TurnCompleted:
                if (_sawTextDelta && !_postedThisTurn)
                {
                    var reply = _buffer.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(reply))
                        await SafePostAsync(reply);
                    else
                        _log.Debug("Turn completed with no buffered reply text");
                }

                _buffer.Clear();
                _sawTextDelta = false;
                _postedThisTurn = false;

                break;
        }
    }

    private async Task SafePostAsync(string text)
    {
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        try
        {
            await _dependencies.ReplyClient.PostThreadReplyAsync(new SlackPostMessage(
                ChannelId: _channelId,
                ThreadTs: _threadTs,
                Text: text));

            _log.Info("Posted Slack reply message");
            ChannelTelemetry.RecordSlackReplyPosted(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed posting Slack reply for session {0}", _sessionId.Value);
            ChannelTelemetry.RecordSlackReplyFailed(_dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private sealed record ThreadOutput(SessionOutput Output);
}
