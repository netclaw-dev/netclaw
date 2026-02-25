using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Channels.Slack;

public sealed class SlackGatewayActor : ReceiveActor
{
    private const int MaxProcessedEventIds = 4096;

    private readonly SlackGatewayDependencies _dependencies;
    private readonly ILoggingAdapter _log;
    private readonly Dictionary<string, byte> _processedEventIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _processedEventOrder = new();

    public SlackGatewayActor(SlackGatewayDependencies dependencies)
    {
        _dependencies = dependencies;
        _log = Context.GetLogger().WithContext("Adapter", "slack");

        Receive<SlackInboundMessage>(message =>
        {
            ChannelTelemetry.RecordSlackEventReceived(message.Kind.ToString());

            if (!TryMarkEventProcessed(message.EventId))
            {
                _log.Debug("Dropping duplicate Slack event {0}", message.EventId);
                ChannelTelemetry.RecordSlackEventDropped("duplicate_event");
                return;
            }

            var actorName = Uri.EscapeDataString(message.ChannelId);
            var conversationProps = _dependencies.ConversationPropsFactory?.Invoke(message.ChannelId, _dependencies)
                ?? SlackConversationActor.CreateProps(message.ChannelId, _dependencies);
            var conversation = Context.Child(actorName)
                .GetOrElse(() => Context.ActorOf(
                    conversationProps,
                    actorName));

            _log.Debug("Routing Slack event {0} to conversation {1}", message.EventId, message.ChannelId);
            ChannelTelemetry.RecordSlackEventRouted(message.Kind.ToString());
            conversation.Forward(message);
        });
    }

    public static Props CreateProps(SlackGatewayDependencies dependencies) =>
        Props.Create(() => new SlackGatewayActor(dependencies));

    private bool TryMarkEventProcessed(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return true;

        if (_processedEventIds.ContainsKey(eventId))
            return false;

        _processedEventIds[eventId] = 0;
        _processedEventOrder.Enqueue(eventId);

        while (_processedEventIds.Count > MaxProcessedEventIds
               && _processedEventOrder.TryDequeue(out var oldestEventId))
            _processedEventIds.Remove(oldestEventId);

        return true;
    }
}

public sealed record SlackGatewayDependencies(
    SessionPipeline Pipeline,
    ActorSystem ActorSystem,
    TimeProvider TimeProvider,
    SlackChannelOptions Options,
    string? BotUserId,
    string? DefaultChannelId,
    ISlackReplyClient ReplyClient,
    Func<string, SlackGatewayDependencies, Props>? ConversationPropsFactory = null,
    Func<SessionId, string, string, SlackGatewayDependencies, Props>? ThreadPropsFactory = null);
