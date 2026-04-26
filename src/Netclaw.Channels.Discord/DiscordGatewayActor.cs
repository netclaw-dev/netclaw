using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Discord;

public sealed class DiscordGatewayActor : ReceiveActor
{
    private const int MaxProcessedEventIds = 4096;

    private readonly DiscordGatewayDependencies _dependencies;
    private readonly ILoggingAdapter _log;
    private readonly Dictionary<DiscordEventId, byte> _processedEventIds = new();
    private readonly Queue<DiscordEventId> _processedEventOrder = new();

    public DiscordGatewayActor(DiscordGatewayDependencies dependencies)
    {
        _dependencies = dependencies;
        _log = Context.GetLogger().WithContext("Adapter", "discord");

        Receive<DiscordGatewayMessage>(message =>
        {
            ChannelTelemetry.RecordDiscordEventReceived("message");

            if (!TryMarkEventProcessed(message.EventId))
            {
                _log.Debug("Dropping duplicate Discord event {0}", message.EventId.Value);
                ChannelTelemetry.RecordDiscordEventFiltered("duplicate_event");
                return;
            }

            var conversation = GetOrCreateConversationActor(message.ChannelId);

            _log.Debug("Routing Discord event {0} to conversation {1}", message.EventId.Value, message.ChannelId);
            conversation.Forward(message);
        });

        Receive<DiscordGatewayInteraction>(interaction =>
        {
            ChannelTelemetry.RecordDiscordEventReceived("interaction");

            var conversation = GetOrCreateConversationActor(interaction.ChannelId);

            _log.Debug("Routing Discord interaction to conversation {0}", interaction.ChannelId);
            conversation.Forward(interaction);
        });

        // No ACL call — audience was validated at reminder mint time by
        // the reminder-audience-authorization capability.
        Receive<DeliverTrustedSessionTurn>(message =>
        {
            if (!TryParseDiscordSessionId(message.SessionId, out var channelId, out _))
            {
                _log.Warning(
                    "Dropping DeliverTrustedSessionTurn with unparseable Discord SessionId {SessionId}",
                    message.SessionId.Value);
                Sender.Tell(CommandNack.For(message.SessionId, "Invalid Discord SessionId format"));
                return;
            }

            var conversation = GetOrCreateConversationActor(channelId);

            _log.Debug(
                "Routing DeliverTrustedSessionTurn session={Session} channel={Channel}",
                message.SessionId.Value, channelId.Value);
            conversation.Forward(message);
        });
    }

    public static Props CreateProps(DiscordGatewayDependencies dependencies) =>
        Props.Create(() => new DiscordGatewayActor(dependencies));

    internal static bool TryParseDiscordSessionId(
        SessionId sessionId,
        out DiscordChannelId channelId,
        out DiscordThreadOrMessageId threadOrMessageId)
    {
        channelId = default;
        threadOrMessageId = default;

        var value = sessionId.Value;
        if (string.IsNullOrEmpty(value))
            return false;

        var slashIdx = value.IndexOf('/', StringComparison.Ordinal);
        if (slashIdx <= 0 || slashIdx == value.Length - 1)
            return false;

        channelId = new DiscordChannelId(value[..slashIdx]);
        threadOrMessageId = new DiscordThreadOrMessageId(value[(slashIdx + 1)..]);
        return true;
    }

    private IActorRef GetOrCreateConversationActor(DiscordChannelId channelId)
    {
        var actorName = Uri.EscapeDataString(channelId.Value);
        var existing = Context.Child(actorName);
        if (!existing.IsNobody())
            return existing;

        var props = _dependencies.ConversationPropsFactory?.Invoke(channelId, _dependencies)
            ?? DiscordConversationActor.CreateProps(channelId, _dependencies);
        return Context.ActorOf(props, actorName);
    }

    private bool TryMarkEventProcessed(DiscordEventId eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId.Value))
        {
            _log.Warning("Rejecting Discord event with empty EventId — cannot deduplicate");
            return false;
        }

        if (!_processedEventIds.TryAdd(eventId, 0))
            return false;

        _processedEventOrder.Enqueue(eventId);

        while (_processedEventIds.Count > MaxProcessedEventIds
               && _processedEventOrder.TryDequeue(out var oldestEventId))
            _processedEventIds.Remove(oldestEventId);

        return true;
    }
}

public sealed record DiscordGatewayDependencies(
    ISessionPipeline Pipeline,
    SessionIngressGate? IngressGate,
    TimeProvider TimeProvider,
    DiscordChannelOptions Options,
    DiscordChannelId? DefaultChannelId,
    IDiscordReplyClient ReplyClient,
    IContentScanner ContentScanner,
    ToolAudienceProfiles AudienceProfiles,
    ModelCapabilities ModelCapabilities,
    NetclawPaths Paths,
    DiscordUserId? BotUserId = null,
    IPromptInjectionDetector? PromptInjectionDetector = null,
    IThreadHistoryFetcher? ThreadHistoryFetcher = null,
    HttpClient? HttpClient = null,
    Func<DiscordChannelId, DiscordGatewayDependencies, Props>? ConversationPropsFactory = null,
    Func<SessionId, DiscordChannelId, DiscordReplyChannelId, DiscordThreadOrMessageId, DiscordMessageId?, DiscordGatewayDependencies, Props>? SessionPropsFactory = null);
