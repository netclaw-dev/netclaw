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
        Receive<Terminated>(HandleTerminated);
    }

    // Fix #6: Stop failing children rather than restarting -- a restart resets
    // _replyChannelId and _threadCreated, corrupting outbound routing.
    // The next inbound message re-creates the binding via GetOrCreateSessionBinding.
    protected override SupervisorStrategy SupervisorStrategy()
        => new OneForOneStrategy(ex =>
        {
            _log.Error(ex, "Session binding child failed; stopping to allow re-creation");
            return Directive.Stop;
        });

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
        // Resolve the actor name, checking the thread alias map for promoted threads
        // so that follow-up messages in a promoted thread find the original binding.
        var (actorName, existingBinding) = ResolveExistingBinding(_channelId, message.ThreadOrMessageId);
        var threadExists = existingBinding is not null;

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
        var normalizedText = NormalizeInboundText(message.Text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            _log.Info("discord_event_filtered event={0} reason=empty_text", message.EventId.Value);
            ChannelTelemetry.RecordDiscordEventFiltered("empty_text");
            return;
        }

        // --- Build session and forward ---
        var sessionId = new SessionId($"{_channelId.Value}/{message.ThreadOrMessageId.Value}");
        var sessionBinding = existingBinding
            ?? GetOrCreateSessionBinding(
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
        // Fix #10: Use _channelId (the conversation actor's canonical channel)
        // rather than interaction.ChannelId which could theoretically differ.
        var sessionBinding = ResolveSessionBinding(_channelId, interaction.ThreadOrMessageId);
        if (sessionBinding is null)
        {
            _log.Info(
                "Ignoring Discord interaction for missing session binding channel={0} threadOrMessage={1}",
                _channelId.Value,
                interaction.ThreadOrMessageId.Value);
            ChannelTelemetry.RecordDiscordInteractionError("missing_session_binding");
            return;
        }

        ChannelTelemetry.RecordDiscordEventRouted("interaction");
        sessionBinding.Forward(new DiscordApprovalResponse(
            ChannelId: _channelId,
            ThreadOrMessageId: interaction.ThreadOrMessageId,
            CallId: interaction.CallId,
            SelectedKey: interaction.SelectedKey,
            SenderId: interaction.SenderId,
            RequesterSenderId: interaction.RequesterSenderId));
    }

    // No ACL call -- audience was validated at reminder mint time.
    // Fix #3: Re-create passivated session bindings instead of dropping the message.
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

        var sessionBinding = GetOrCreateSessionBinding(
            message.SessionId,
            _channelId,
            new DiscordReplyChannelId(ResolveReplyChannelForTrustedTurn(threadOrMessageId)),
            threadOrMessageId,
            rootMessageId: null);

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

    // Fix #9: Clean up stale thread aliases when session bindings stop.
    private void HandleTerminated(Terminated msg)
    {
        var deadName = msg.ActorRef.Path.Name;
        var keysToRemove = new List<string>();
        foreach (var (key, value) in _threadAliases)
        {
            if (string.Equals(value, deadName, StringComparison.Ordinal))
                keysToRemove.Add(key);
        }

        foreach (var key in keysToRemove)
            _threadAliases.Remove(key);
    }

    /// <summary>
    /// Strips the bot mention tag from inbound text and trims whitespace,
    /// mirroring Slack's NormalizeInboundText behavior.
    /// </summary>
    private string NormalizeInboundText(string text)
    {
        if (_dependencies.BotUserId is not { } botUserId)
            return text.Trim();

        var mention = $"<@{botUserId.Value}>";
        return text.Replace(mention, string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// Resolves the best reply channel ID for a trusted session turn. If the
    /// thread was promoted, uses the thread channel ID; otherwise falls back
    /// to the thread-or-message ID (which is the channel itself for DMs).
    /// </summary>
    private string ResolveReplyChannelForTrustedTurn(DiscordThreadOrMessageId threadOrMessageId)
    {
        // Check if there is a thread alias whose original actor name matches this threadOrMessageId.
        // If so, the reply channel is the promoted thread channel ID (the alias key).
        var originalActorName = BuildActorName(_channelId, threadOrMessageId);
        foreach (var (threadChannelId, aliasedActorName) in _threadAliases)
        {
            if (string.Equals(aliasedActorName, originalActorName, StringComparison.Ordinal))
                return threadChannelId;
        }

        return threadOrMessageId.Value;
    }

    /// <summary>
    /// Looks up a session binding actor by direct child name, then falls back to
    /// the thread-alias map for promoted thread channel IDs. Returns null if no
    /// matching live actor exists.
    /// </summary>
    private IActorRef? ResolveSessionBinding(DiscordChannelId channelId, DiscordThreadOrMessageId threadOrMessageId)
    {
        var (_, binding) = ResolveExistingBinding(channelId, threadOrMessageId);
        return binding;
    }

    /// <summary>
    /// Looks up a session binding actor by direct child name, then falls back to
    /// the thread-alias map for promoted thread channel IDs. Returns both the
    /// resolved actor name and the actor ref (or null if no live actor matches).
    /// </summary>
    private (string ActorName, IActorRef? Binding) ResolveExistingBinding(
        DiscordChannelId channelId, DiscordThreadOrMessageId threadOrMessageId)
    {
        var actorName = BuildActorName(channelId, threadOrMessageId);
        var direct = Context.Child(actorName);
        if (!direct.IsNobody())
            return (actorName, direct);

        if (_threadAliases.TryGetValue(threadOrMessageId.Value, out var aliasedActorName))
        {
            var aliased = Context.Child(aliasedActorName);
            if (!aliased.IsNobody())
                return (aliasedActorName, aliased);
        }

        return (actorName, null);
    }

    private IActorRef GetOrCreateSessionBinding(
        SessionId sessionId,
        DiscordChannelId channelId,
        DiscordReplyChannelId replyChannelId,
        DiscordThreadOrMessageId threadOrMessageId,
        DiscordMessageId? rootMessageId)
    {
        // Check alias map first so promoted thread messages find the original binding
        var (resolvedName, existing) = ResolveExistingBinding(channelId, threadOrMessageId);
        if (existing is not null)
            return existing;

        var props = _dependencies.SessionPropsFactory?.Invoke(
                        sessionId, channelId, replyChannelId, threadOrMessageId, rootMessageId, _dependencies)
                    ?? DiscordSessionBindingActor.CreateProps(
                        sessionId, channelId, replyChannelId, threadOrMessageId, rootMessageId, _dependencies);
        var child = Context.ActorOf(props, resolvedName);
        Context.Watch(child);
        return child;
    }

    private static string BuildActorName(DiscordChannelId channelId, DiscordThreadOrMessageId threadOrMessageId)
        => Uri.EscapeDataString($"{channelId.Value}:{threadOrMessageId.Value}");
}
