using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Slack;

public sealed class SlackGatewayActor : ReceiveActor
{
    private const int MaxProcessedEventIds = 4096;

    private readonly SlackGatewayDependencies _dependencies;
    private readonly Dictionary<string, byte> _processedEventIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _processedEventOrder = new();

    public SlackGatewayActor(SlackGatewayDependencies dependencies)
    {
        _dependencies = dependencies;

        Receive<SlackInboundMessage>(message =>
        {
            if (!TryMarkEventProcessed(message.EventId))
                return;

            var actorName = Uri.EscapeDataString(message.ChannelId);
            var conversationProps = _dependencies.ConversationPropsFactory?.Invoke(message.ChannelId, _dependencies)
                ?? SlackConversationActor.CreateProps(message.ChannelId, _dependencies);
            var conversation = Context.Child(actorName)
                .GetOrElse(() => Context.ActorOf(
                    conversationProps,
                    actorName));

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
    Func<SlackPostMessage, Task> PostMessageAsync,
    Func<string, SlackGatewayDependencies, Props>? ConversationPropsFactory = null,
    Func<SessionId, string, string, SlackGatewayDependencies, Props>? ThreadPropsFactory = null);
