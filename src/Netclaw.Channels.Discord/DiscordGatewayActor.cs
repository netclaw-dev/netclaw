using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
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

            if (message.IsBotMessage)
            {
                _log.Info("discord_event_filtered event={0} reason=bot_message", message.EventId.Value);
                ChannelTelemetry.RecordDiscordEventFiltered("bot_message");
                return;
            }

            if (_dependencies.IngressGate?.ClosedReason is { } closedReason)
            {
                _log.Info("discord_event_filtered event={0} reason=restart_drain_active", message.EventId.Value);
                ChannelTelemetry.RecordDiscordEventFiltered("restart_drain_active");
                _log.Debug("Ingress closed reason: {0}", closedReason);
                return;
            }

            if (string.IsNullOrWhiteSpace(message.Text))
            {
                _log.Info("discord_event_filtered event={0} reason=no_content", message.EventId.Value);
                ChannelTelemetry.RecordDiscordEventFiltered("no_content");
                return;
            }

            var aclDecision = DiscordAclPolicy.EvaluateInbound(
                message,
                _dependencies.Options,
                _dependencies.DefaultChannelId);

            if (!aclDecision.IsAllowed)
            {
                var reason = aclDecision.DenyReason ?? "acl_denied";
                _log.Info("discord_event_dropped event={0} reason={1}", message.EventId.Value, reason);
                ChannelTelemetry.RecordDiscordEventDropped(reason);
                return;
            }

            var sessionId = new SessionId($"{message.ChannelId.Value}/{message.ThreadOrMessageId.Value}");
            var sessionBinding = GetOrCreateSessionBinding(
                sessionId,
                message.ChannelId,
                message.ReplyChannelId,
                message.ThreadOrMessageId,
                message.RootMessageId);

            ChannelTelemetry.RecordDiscordEventRouted("message");
            sessionBinding.Forward(new DiscordThreadInbound(
                SessionId: sessionId,
                ChannelId: message.ChannelId,
                ReplyChannelId: message.ReplyChannelId,
                ThreadOrMessageId: message.ThreadOrMessageId,
                RootMessageId: message.RootMessageId,
                EventId: message.EventId,
                SenderId: message.SenderId,
                Audience: aclDecision.Audience,
                Principal: aclDecision.Principal,
                Provenance: aclDecision.Provenance,
                Text: message.Text,
                ReceivedAt: message.ReceivedAt));
        });

        Receive<DiscordGatewayInteraction>(interaction =>
        {
            ChannelTelemetry.RecordDiscordEventReceived("interaction");

            if (!IsInteractionAuthorized(interaction))
            {
                ChannelTelemetry.RecordDiscordEventDropped("interaction_acl_denied");
                return;
            }

            var actorName = BuildActorName(interaction.ChannelId, interaction.ThreadOrMessageId);
            var sessionBinding = Context.Child(actorName);
            if (sessionBinding.IsNobody())
            {
                _log.Info(
                    "Ignoring Discord interaction for missing session binding channel={0} threadOrMessage={1}",
                    interaction.ChannelId.Value,
                    interaction.ThreadOrMessageId.Value);
                ChannelTelemetry.RecordDiscordInteractionError("missing_session_binding");
                return;
            }

            ChannelTelemetry.RecordDiscordEventRouted("interaction");
            sessionBinding.Forward(new DiscordApprovalResponse(
                ChannelId: interaction.ChannelId,
                ThreadOrMessageId: interaction.ThreadOrMessageId,
                CallId: interaction.CallId,
                SelectedKey: interaction.SelectedKey,
                SenderId: interaction.SenderId,
                RequesterSenderId: interaction.RequesterSenderId));
        });

        Receive<DeliverTrustedSessionTurn>(message =>
        {
            if (!TryParseDiscordSessionId(message.SessionId, out var channelId, out var threadOrMessageId))
            {
                _log.Warning(
                    "Dropping DeliverTrustedSessionTurn with unparseable Discord SessionId {SessionId}",
                    message.SessionId.Value);
                Sender.Tell(CommandNack.For(message.SessionId, "Invalid Discord SessionId format"));
                return;
            }

            var actorName = BuildActorName(channelId, threadOrMessageId);
            var sessionBinding = Context.Child(actorName);
            if (sessionBinding.IsNobody())
            {
                _log.Warning(
                    "Dropping DeliverTrustedSessionTurn for missing session binding session={Session}",
                    message.SessionId.Value);
                Sender.Tell(CommandNack.For(message.SessionId, "No active Discord session binding"));
                return;
            }

            _log.Debug(
                "Routing DeliverTrustedSessionTurn session={Session} channel={Channel} threadOrMessage={ThreadOrMessage}",
                message.SessionId.Value, channelId.Value, threadOrMessageId.Value);
            sessionBinding.Forward(message);
        });
    }

    public static Props CreateProps(DiscordGatewayDependencies dependencies) =>
        Props.Create(() => new DiscordGatewayActor(dependencies));

    private IActorRef GetOrCreateSessionBinding(
        SessionId sessionId,
        DiscordChannelId channelId,
        DiscordReplyChannelId replyChannelId,
        DiscordThreadOrMessageId threadOrMessageId,
        DiscordMessageId? rootMessageId)
    {
        var actorName = BuildActorName(channelId, threadOrMessageId);
        var existing = Context.Child(actorName);
        if (!existing.IsNobody())
            return existing;

        var props = _dependencies.SessionPropsFactory?.Invoke(
                        sessionId, channelId, replyChannelId, threadOrMessageId, rootMessageId, _dependencies)
                    ?? DiscordSessionBindingActor.CreateProps(
                        sessionId, channelId, replyChannelId, threadOrMessageId, rootMessageId, _dependencies);
        return Context.ActorOf(props, actorName);
    }

    private bool IsInteractionAuthorized(DiscordGatewayInteraction interaction)
    {
        if (string.IsNullOrWhiteSpace(interaction.SenderId.Value))
        {
            _log.Info("discord_interaction_dropped reason=missing_user_id");
            return false;
        }

        if (!DiscordAclPolicy.IsAllowedChannel(interaction.ChannelId, _dependencies.Options, _dependencies.DefaultChannelId))
        {
            _log.Info("discord_interaction_dropped channel={0} reason=channel_not_allowed", interaction.ChannelId.Value);
            return false;
        }

        if (_dependencies.Options.AllowedUserIds.Length > 0
            && !_dependencies.Options.AllowedUserIds.Contains(interaction.SenderId.Value, StringComparer.Ordinal))
        {
            _log.Info("discord_interaction_dropped user={0} reason=user_not_allowed", interaction.SenderId.Value);
            return false;
        }

        return true;
    }

    private static string BuildActorName(DiscordChannelId channelId, DiscordThreadOrMessageId threadOrMessageId)
        => $"{channelId.Value}:{threadOrMessageId.Value}";

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

    private bool TryMarkEventProcessed(DiscordEventId eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId.Value))
            return true;

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
    IPromptInjectionDetector? PromptInjectionDetector = null,
    Func<SessionId, DiscordChannelId, DiscordReplyChannelId, DiscordThreadOrMessageId, DiscordMessageId?, DiscordGatewayDependencies, Props>? SessionPropsFactory = null);
