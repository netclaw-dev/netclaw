// -----------------------------------------------------------------------
// <copyright file="MattermostGatewayActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Mattermost;

public sealed class MattermostGatewayActor : ReceiveActor
{
    private const int MaxProcessedEventIds = 4096;

    private readonly MattermostGatewayDependencies _dependencies;
    private readonly ILoggingAdapter _log;
    private readonly Dictionary<MattermostEventId, byte> _processedEventIds = [];
    private readonly Queue<MattermostEventId> _processedEventOrder = new();

    public MattermostGatewayActor(MattermostGatewayDependencies dependencies)
    {
        _dependencies = dependencies;
        _log = Context.GetLogger().WithContext("Adapter", "mattermost");

        Receive<MattermostGatewayMessage>(message =>
        {
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventReceived("message");

            if (!TryMarkEventProcessed(message.EventId))
            {
                _log.Debug("Dropping duplicate Mattermost event {0}", message.EventId.Value);
                ChannelTelemetry.For(ChannelType.Mattermost).RecordEventFiltered("duplicate_event");
                return;
            }

            var conversation = GetOrCreateConversationActor(message.ChannelId);

            _log.Debug("Routing Mattermost event {0} to conversation {1}", message.EventId.Value, message.ChannelId);
            conversation.Forward(message);
        });

        Receive<MattermostGatewayInteraction>(interaction =>
        {
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventReceived("interaction");

            var conversation = GetOrCreateConversationActor(interaction.ChannelId);

            _log.Debug("Routing Mattermost interaction to conversation {0}", interaction.ChannelId);
            conversation.Forward(interaction);
        });

        Receive<StartMattermostProactiveThread>(message =>
        {
            var conversation = GetOrCreateConversationActor(message.ChannelId);

            _log.Debug(
                "Routing StartMattermostProactiveThread session={Session} channel={Channel} rootPost={RootPost}",
                message.SessionId.Value, message.ChannelId.Value, message.RootPostId.Value);
            conversation.Forward(message);
        });

        Receive<DeliverTrustedSessionTurn>(message =>
        {
            if (!TryParseMattermostSessionId(message.SessionId, out var channelId, out _))
            {
                _log.Warning(
                    "Dropping DeliverTrustedSessionTurn with unparseable Mattermost SessionId {SessionId}",
                    message.SessionId.Value);
                Sender.Tell(CommandNack.For(message.SessionId, "Invalid Mattermost SessionId format"));
                return;
            }

            var conversation = GetOrCreateConversationActor(channelId);

            _log.Debug(
                "Routing DeliverTrustedSessionTurn session={Session} channel={Channel}",
                message.SessionId.Value, channelId.Value);
            conversation.Forward(message);
        });
    }

    public static Props CreateProps(MattermostGatewayDependencies dependencies) =>
        Props.Create(() => new MattermostGatewayActor(dependencies));

    internal static bool TryParseMattermostSessionId(
        SessionId sessionId,
        out MattermostChannelId channelId,
        out MattermostRootPostId rootPostId)
    {
        channelId = default;
        rootPostId = default;

        var value = sessionId.Value;
        if (string.IsNullOrEmpty(value))
            return false;

        var slashIdx = value.IndexOf('/', StringComparison.Ordinal);
        if (slashIdx <= 0 || slashIdx == value.Length - 1)
            return false;

        channelId = new MattermostChannelId(value[..slashIdx]);
        rootPostId = new MattermostRootPostId(value[(slashIdx + 1)..]);
        return true;
    }

    private IActorRef GetOrCreateConversationActor(MattermostChannelId channelId)
    {
        var actorName = Uri.EscapeDataString(channelId.Value);
        var existing = Context.Child(actorName);
        if (!existing.IsNobody())
            return existing;

        var props = _dependencies.ConversationPropsFactory?.Invoke(channelId, _dependencies)
            ?? MattermostConversationActor.CreateProps(channelId, _dependencies);
        return Context.ActorOf(props, actorName);
    }

    private bool TryMarkEventProcessed(MattermostEventId eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId.Value))
        {
            _log.Warning("Rejecting Mattermost event with empty EventId — cannot deduplicate");
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

public sealed record MattermostGatewayDependencies(
    ISessionPipeline Pipeline,
    SessionIngressGate? IngressGate,
    TimeProvider TimeProvider,
    MattermostChannelOptions Options,
    MattermostChannelId? DefaultChannelId,
    IMattermostReplyClient ReplyClient,
    IContentScanner ContentScanner,
    ToolAudienceProfiles AudienceProfiles,
    ModelCapabilities ModelCapabilities,
    NetclawPaths Paths,
    string? ServerUrl = null,
    string? CallbackUrl = null,
    MattermostUserId? BotUserId = null,
    string? BotUsername = null,
    IPromptInjectionDetector? PromptInjectionDetector = null,
    IThreadHistoryFetcher? ThreadHistoryFetcher = null,
    byte[]? CallbackSigningKey = null,
    HttpClient? HttpClient = null,
    Func<MattermostChannelId, MattermostGatewayDependencies, Props>? ConversationPropsFactory = null,
    Func<SessionId, MattermostChannelId, MattermostRootPostId, MattermostGatewayDependencies, Props>? SessionPropsFactory = null);
