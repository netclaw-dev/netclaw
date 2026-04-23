using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;

namespace Netclaw.Channels.Discord;

/// <summary>
/// Per-channel actor that serves as the security boundary for Discord messages.
/// Performs ACL checks, routing policy evaluation, ingress gating, and manages
/// the thread-ID alias map so that messages arriving on a promoted thread channel
/// route back to the original session binding actor.
/// </summary>
internal sealed class DiscordConversationActor : ReceiveActor
{
    private const int MaxThreadAliases = 4096;

    private readonly DiscordChannelId _channelId;
    private readonly DiscordGatewayDependencies _dependencies;
    private readonly ILoggingAdapter _log;

    private readonly Dictionary<string, string> _threadAliases = new(StringComparer.Ordinal);
    private readonly Queue<string> _threadAliasOrder = new();

    public DiscordConversationActor(DiscordChannelId channelId, DiscordGatewayDependencies dependencies)
    {
        _channelId = channelId;
        _dependencies = dependencies;
        _log = Context.GetLogger()
            .WithContext("Adapter", "discord")
            .WithContext("DiscordChannelId", _channelId.Value);

        Context.SetReceiveTimeout(TimeSpan.FromHours(2));

        Receive<ReceiveTimeout>(_ =>
        {
            _log.Info("Discord conversation idle for 2 hours, passivating");
            Context.Stop(Self);
        });

        Receive<DiscordGatewayMessage>(HandleGatewayMessage);
        Receive<DiscordGatewayInteraction>(HandleGatewayInteraction);
        Receive<DeliverTrustedSessionTurn>(HandleTrustedSessionTurn);
        Receive<ThreadPromoted>(HandleThreadPromoted);
    }

    public static Props CreateProps(DiscordChannelId channelId, DiscordGatewayDependencies dependencies)
        => Props.Create(() => new DiscordConversationActor(channelId, dependencies));

    private void HandleGatewayMessage(DiscordGatewayMessage message)
    {
        var options = _dependencies.Options;

        // --- ACL gate ---
        var aclDecision = DiscordAclPolicy.EvaluateInbound(
            message,
            options,
            _dependencies.DefaultChannelId);

        if (!aclDecision.IsAllowed)
        {
            var reason = aclDecision.DenyReason ?? "acl_denied";
            _log.Info("discord_event_dropped event={0} reason={1}", message.EventId.Value, reason);
            ChannelTelemetry.RecordDiscordEventDropped(reason);
            return;
        }

        // --- Bot self-loop filter ---
        if (message.IsBotMessage)
        {
            _log.Info("discord_event_filtered event={0} reason=bot_message", message.EventId.Value);
            ChannelTelemetry.RecordDiscordEventFiltered("bot_message");
            return;
        }

        // --- Ingress gate ---
        if (_dependencies.IngressGate?.ClosedReason is { } closedReason)
        {
            _log.Info("discord_event_filtered event={0} reason=restart_drain_active", message.EventId.Value);
            ChannelTelemetry.RecordDiscordEventFiltered("restart_drain_active");
            _log.Debug("Ingress closed reason: {0}", closedReason);
            return;
        }

        // --- Routing policy ---
        var actorName = BuildActorName(_channelId, message.ThreadOrMessageId);
        var existingBinding = Context.Child(actorName);
        var threadExists = !existingBinding.IsNobody();

        var decision = DiscordRoutingPolicy.Evaluate(
            message,
            options.MentionOnly,
            options.AllowDirectMessages,
            options.MentionRequiredInDm,
            threadExists,
            message.ContainsBotMention);

        if (decision.Kind is DiscordRoutingDecisionKind.Ignore)
        {
            var ignoreReason = decision.IgnoreReason!.Value;
            _log.Info(
                "discord_event_filtered event={0} reason=routing_policy_ignore ignoreReason={1}",
                message.EventId.Value,
                ignoreReason);
            ChannelTelemetry.RecordDiscordEventFiltered(
                DiscordRoutingDecision.TelemetryLabelFor(ignoreReason));
            return;
        }

        if (decision.Kind is DiscordRoutingDecisionKind.ContinueOnly && !threadExists)
        {
            _log.Info("discord_event_dropped event={0} reason=thread_not_initialized", message.EventId.Value);
            ChannelTelemetry.RecordDiscordEventDropped("thread_not_initialized");
            return;
        }

        // --- Empty text filter ---
        var normalizedText = message.Text.Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            _log.Info("discord_event_filtered event={0} reason=empty_text", message.EventId.Value);
            ChannelTelemetry.RecordDiscordEventFiltered("empty_text");
            return;
        }

        // --- Build session and forward ---
        var sessionId = new SessionId($"{_channelId.Value}/{message.ThreadOrMessageId.Value}");
        var sessionBinding = GetOrCreateSessionBinding(
            sessionId,
            _channelId,
            message.ReplyChannelId,
            message.ThreadOrMessageId,
            message.RootMessageId);

        var turnId = string.IsNullOrWhiteSpace(message.EventId.Value)
            ? IdGen.ShortId()
            : message.EventId.Value;

        var log = _log
            .WithContext("DiscordThreadOrMessageId", message.ThreadOrMessageId.Value)
            .WithContext("SessionId", sessionId.Value)
            .WithContext("TurnId", turnId)
            .WithContext("DiscordEventId", message.EventId.Value);

        log.Info("discord_turn_routed event={EventId} textChars={TextLength}",
            message.EventId.Value,
            normalizedText.Length);

        ChannelTelemetry.RecordDiscordEventRouted("message");
        sessionBinding.Forward(new DiscordThreadInbound(
            SessionId: sessionId,
            ChannelId: _channelId,
            ReplyChannelId: message.ReplyChannelId,
            ThreadOrMessageId: message.ThreadOrMessageId,
            RootMessageId: message.RootMessageId,
            EventId: message.EventId,
            SenderId: message.SenderId,
            Audience: aclDecision.Audience,
            Principal: aclDecision.Principal,
            Provenance: aclDecision.Provenance,
            Text: normalizedText,
            ReceivedAt: message.ReceivedAt));
    }

    private void HandleGatewayInteraction(DiscordGatewayInteraction interaction)
    {
        ChannelTelemetry.RecordDiscordEventReceived("interaction");

        var sessionBinding = ResolveSessionBinding(interaction.ChannelId, interaction.ThreadOrMessageId);
        if (sessionBinding is null)
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
    }

    // No ACL call -- audience was validated at reminder mint time.
    private void HandleTrustedSessionTurn(DeliverTrustedSessionTurn message)
    {
        if (!DiscordGatewayActor.TryParseDiscordSessionId(
                message.SessionId,
                out var parsedChannelId,
                out var threadOrMessageId))
        {
            _log.Warning(
                "Dropping DeliverTrustedSessionTurn with unparseable Discord SessionId {SessionId}",
                message.SessionId.Value);
            Sender.Tell(CommandNack.For(message.SessionId, "Invalid Discord SessionId format"));
            return;
        }

        if (parsedChannelId != _channelId)
        {
            _log.Warning(
                "Dropping DeliverTrustedSessionTurn for wrong conversation session={Session} expected_channel={Channel}",
                message.SessionId.Value, _channelId.Value);
            Sender.Tell(CommandNack.For(message.SessionId, "Conversation mismatch"));
            return;
        }

        var sessionBinding = ResolveSessionBinding(_channelId, threadOrMessageId);
        if (sessionBinding is null)
        {
            _log.Warning(
                "Dropping DeliverTrustedSessionTurn for missing session binding session={Session}",
                message.SessionId.Value);
            Sender.Tell(CommandNack.For(message.SessionId, "No active Discord session binding"));
            return;
        }

        _log.Debug(
            "Routing DeliverTrustedSessionTurn session={Session} channel={Channel} threadOrMessage={ThreadOrMessage}",
            message.SessionId.Value, parsedChannelId.Value, threadOrMessageId.Value);
        sessionBinding.Forward(message);
    }

    private void HandleThreadPromoted(ThreadPromoted msg)
    {
        var originalActorName = BuildActorName(_channelId, msg.OriginalThreadOrMessageId);
        _threadAliases[msg.ThreadChannelId.Value] = originalActorName;
        _threadAliasOrder.Enqueue(msg.ThreadChannelId.Value);

        while (_threadAliases.Count > MaxThreadAliases
               && _threadAliasOrder.TryDequeue(out var oldest))
            _threadAliases.Remove(oldest);

        _log.Info(
            "Registered thread alias thread_channel={ThreadChannel} -> actor={ActorName}",
            msg.ThreadChannelId.Value,
            originalActorName);
    }

    /// <summary>
    /// Looks up a session binding actor by direct child name, then falls back to
    /// the thread-alias map for promoted thread channel IDs.
    /// </summary>
    private IActorRef? ResolveSessionBinding(DiscordChannelId channelId, DiscordThreadOrMessageId threadOrMessageId)
    {
        var actorName = BuildActorName(channelId, threadOrMessageId);
        var direct = Context.Child(actorName);
        if (!direct.IsNobody())
            return direct;

        // Check if the threadOrMessageId is actually a promoted thread channel ID
        if (_threadAliases.TryGetValue(threadOrMessageId.Value, out var aliasedActorName))
        {
            var aliased = Context.Child(aliasedActorName);
            if (!aliased.IsNobody())
                return aliased;
        }

        return null;
    }

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

    private static string BuildActorName(DiscordChannelId channelId, DiscordThreadOrMessageId threadOrMessageId)
        => $"{channelId.Value}:{threadOrMessageId.Value}";
}
