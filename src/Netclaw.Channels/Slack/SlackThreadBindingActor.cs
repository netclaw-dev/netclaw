using System.Text;
using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Slack;

internal sealed class SlackThreadBindingActor : ReceiveActor
{
    private readonly SessionId _sessionId;
    private readonly string _channelId;
    private readonly string _threadTs;
    private readonly SlackGatewayDependencies _dependencies;

    private readonly StringBuilder _buffer = new();

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
            Context.Stop(Self);
    }

    private async Task EnsureInitializedAsync()
    {
        if (_session is not null)
            return;

        var materialized = await _dependencies.Pipeline.CreateAsync(_sessionId, new SessionPipelineOptions
        {
            ChannelType = "slack",
            Filter = OutputFilter.Full
        });

        var inputQueue = Source.Queue<ChannelInput>(32, OverflowStrategy.Backpressure)
            .ToMaterialized(materialized.Input, Keep.Left)
            .Run(_dependencies.ActorSystem);

        materialized.Output
            .To(Sink.ForEach<SessionOutput>(output => Self.Tell(new ThreadOutput(output))))
            .Run(_dependencies.ActorSystem);

        _session = materialized;
        _inputQueue = inputQueue;
    }

    private async Task HandleOutputAsync(ThreadOutput threadOutput)
    {
        switch (threadOutput.Output)
        {
            case TextDeltaOutput delta:
                _buffer.Append(delta.Delta);
                break;

            case TextOutput text when _buffer.Length == 0:
                _buffer.Append(text.Text);
                break;

            case ErrorOutput err:
                await _dependencies.PostMessageAsync(new SlackPostMessage(
                    ChannelId: _channelId,
                    ThreadTs: _threadTs,
                    Text: $":warning: {err.Message}"));
                _buffer.Clear();
                break;

            case TurnCompleted:
                var reply = _buffer.ToString().Trim();
                _buffer.Clear();
                if (!string.IsNullOrWhiteSpace(reply))
                {
                    await _dependencies.PostMessageAsync(new SlackPostMessage(
                        ChannelId: _channelId,
                        ThreadTs: _threadTs,
                        Text: reply));
                }

                break;
        }
    }

    private sealed record ThreadOutput(SessionOutput Output);
}
