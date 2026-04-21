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

            var actorName = BuildActorName(message.ChannelId, message.ThreadOrMessageId);
            var existing = Context.Child(actorName);
            var sessionId = new SessionId($"{message.ChannelId.Value}/{message.ThreadOrMessageId.Value}");
            var props = _dependencies.SessionPropsFactory?.Invoke(
                            sessionId,
                            message.ChannelId,
                            message.ReplyChannelId,
                            message.ThreadOrMessageId,
                            message.RootMessageId,
                            _dependencies)
                        ?? DiscordSessionBindingActor.CreateProps(
                            sessionId,
                            message.ChannelId,
                            message.ReplyChannelId,
                            message.ThreadOrMessageId,
                            message.RootMessageId,
                            _dependencies);

            var sessionBinding = existing.IsNobody()
                ? Context.ActorOf(props, actorName)
                : existing;

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

            sessionBinding.Forward(new DiscordApprovalResponse(
                ChannelId: interaction.ChannelId,
                ThreadOrMessageId: interaction.ThreadOrMessageId,
                CallId: interaction.CallId,
                SelectedKey: interaction.SelectedKey,
                SenderId: interaction.SenderId,
                RequesterSenderId: interaction.RequesterSenderId));
        });

        // No ACL call — audience was validated at reminder mint time by
        // the reminder-audience-authorization capability.
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
            var replyChannelId = new DiscordReplyChannelId(threadOrMessageId.Value);
            var props = _dependencies.SessionPropsFactory?.Invoke(
                            message.SessionId,
                            channelId,
                            replyChannelId,
                            threadOrMessageId,
                            null,
                            _dependencies)
                        ?? DiscordSessionBindingActor.CreateProps(
                            message.SessionId,
                            channelId,
                            replyChannelId,
                            threadOrMessageId,
                            null,
                            _dependencies);

            var existing = Context.Child(actorName);
            var sessionBinding = existing.IsNobody()
                ? Context.ActorOf(props, actorName)
                : existing;

            _log.Debug(
                "Routing DeliverTrustedSessionTurn session={Session} channel={Channel} threadOrMessage={ThreadOrMessage}",
                message.SessionId.Value, channelId.Value, threadOrMessageId.Value);
            sessionBinding.Forward(message);
        });
    }

    public static Props CreateProps(DiscordGatewayDependencies dependencies) =>
        Props.Create(() => new DiscordGatewayActor(dependencies));

    private static string BuildActorName(DiscordChannelId channelId, DiscordThreadOrMessageId threadOrMessageId)
        => Uri.EscapeDataString($"{channelId.Value}:{threadOrMessageId.Value}");

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

public sealed record DiscordGatewayDependencies(
    ISessionPipeline Pipeline,
    SessionIngressGate? IngressGate,
    TimeProvider TimeProvider,
    DiscordChannelOptions Options,
    DiscordChannelId? DefaultChannelId,
    IDiscordReplyClient ReplyClient,
    IPromptInjectionDetector? PromptInjectionDetector = null,
    Func<SessionId, DiscordChannelId, DiscordReplyChannelId, DiscordThreadOrMessageId, DiscordMessageId?, DiscordGatewayDependencies, Props>? SessionPropsFactory = null);
