// -----------------------------------------------------------------------
// <copyright file="DiscordConversationActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;

namespace Netclaw.Channels.Discord;

/// <summary>
/// Per-channel actor that serves as the security boundary for Discord messages.
/// Performs ACL checks, routing policy evaluation, and ingress gating.
/// Uses blind-write routing: session IDs are derived deterministically from
/// message identifiers with no routing state.
/// </summary>
internal sealed class DiscordConversationActor : ReceiveActor
{
    private const int MaxInboundTextLength = 4000;

    private readonly DiscordChannelId _channelId;
    private readonly DiscordGatewayDependencies _dependencies;
    private readonly string? _botMentionTag;
    private readonly ILoggingAdapter _log;

    public DiscordConversationActor(DiscordChannelId channelId, DiscordGatewayDependencies dependencies)
    {
        _channelId = channelId;
        _dependencies = dependencies;
        _botMentionTag = dependencies.BotUserId is { } botId ? $"<@{botId.Value}>" : null;
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
        Receive<StartProactiveThread>(HandleProactiveThread);
        Receive<Terminated>(HandleTerminated);
    }

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
            ChannelTelemetry.For(ChannelType.Discord).RecordEventDropped(reason);
            return;
        }

        // --- Bot self-loop filter ---
        if (message.IsBotMessage)
        {
            _log.Info("discord_event_filtered event={0} reason=bot_message", message.EventId.Value);
            ChannelTelemetry.For(ChannelType.Discord).RecordEventFiltered("bot_message");
            return;
        }

        // --- Ingress gate ---
        if (_dependencies.IngressGate?.ClosedReason is { } closedReason)
        {
            _log.Info("discord_event_filtered event={0} reason=restart_drain_active", message.EventId.Value);
            ChannelTelemetry.For(ChannelType.Discord).RecordEventFiltered("restart_drain_active");
            // Safe fire-and-forget: PostIngressClosedReplyAsync wraps everything in try/catch,
            // and no synchronous code precedes the first await, so exceptions cannot escape.
            _ = PostIngressClosedReplyAsync(message.ReplyChannelId, closedReason);
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
            ChannelTelemetry.For(ChannelType.Discord).RecordEventFiltered(
                DiscordRoutingDecision.TelemetryLabelFor(ignoreReason));
            return;
        }

        if (decision.Kind is DiscordRoutingDecisionKind.ContinueOnly && !threadExists)
        {
            _log.Info("discord_event_dropped event={0} reason=thread_not_initialized", message.EventId.Value);
            ChannelTelemetry.For(ChannelType.Discord).RecordEventDropped("thread_not_initialized");
            return;
        }

        // --- Empty text filter ---
        var normalizedText = NormalizeInboundText(message.Text);
        if (normalizedText.Length > MaxInboundTextLength)
        {
            _log.Warning("discord_inbound_text_truncated original={OriginalLength} clamped={MaxLength}",
                normalizedText.Length, MaxInboundTextLength);
            normalizedText = normalizedText[..MaxInboundTextLength];
        }
        var hasAttachments = message.Attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(normalizedText) && !hasAttachments)
        {
            _log.Info("discord_event_filtered event={0} reason=empty_text", message.EventId.Value);
            ChannelTelemetry.For(ChannelType.Discord).RecordEventFiltered("empty_text");
            return;
        }

        // --- Build session and forward ---
        var sessionId = new SessionId($"{_channelId.Value}/{message.ThreadOrMessageId.Value}");
        var sessionBinding = threadExists
            ? existingBinding
            : GetOrCreateSessionBinding(
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

        ChannelTelemetry.For(ChannelType.Discord).RecordEventRouted("message");
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
            ReceivedAt: message.ReceivedAt,
            Attachments: message.Attachments));
    }

    private void HandleGatewayInteraction(DiscordGatewayInteraction interaction)
    {
        // Lazy-spawn the session binding when missing. The interaction payload carries
        // everything needed to address the session deterministically; a passivated/cold
        // child must not silently drop the response. See issue #979 for the production
        // passivation incident (symmetric with Slack's conversation/thread tree).
        //
        // Prefer the explicit ReplyChannelId carried on the interaction. For top-level
        // guild prompts ThreadOrMessageId is the prompt's *message* ID, not a channel
        // ID, so deriving the reply channel from it silently broke chat.update on the
        // cold-spawn redraw path. See issue #939.
        var replyChannelId = interaction.ReplyChannelId
            ?? new DiscordReplyChannelId(interaction.ThreadOrMessageId.Value);
        var sessionId = new SessionId($"{_channelId.Value}/{interaction.ThreadOrMessageId.Value}");
        var sessionBinding = GetOrCreateSessionBinding(
            sessionId,
            _channelId,
            replyChannelId,
            interaction.ThreadOrMessageId,
            rootMessageId: null);

        ChannelTelemetry.For(ChannelType.Discord).RecordEventRouted("interaction");
        sessionBinding.Forward(new DiscordApprovalResponse(
            ChannelId: _channelId,
            ThreadOrMessageId: interaction.ThreadOrMessageId,
            CallId: new Netclaw.Tools.ToolCallId(interaction.CallId),
            SelectedKey: interaction.SelectedKey,
            SenderId: interaction.SenderId,
            RequesterSenderId: interaction.RequesterSenderId,
            PromptMessageId: interaction.PromptMessageId));
    }

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

        var replyChannelId = new DiscordReplyChannelId(threadOrMessageId.Value);
        var sessionBinding = GetOrCreateSessionBinding(
            message.SessionId,
            _channelId,
            replyChannelId,
            threadOrMessageId,
            rootMessageId: null);

        _log.Debug(
            "Routing DeliverTrustedSessionTurn session={Session} channel={Channel} threadOrMessage={ThreadOrMessage}",
            message.SessionId.Value, parsedChannelId.Value, threadOrMessageId.Value);
        sessionBinding.Forward(message);
    }

    private void HandleProactiveThread(StartProactiveThread message)
    {
        if (_dependencies.IngressGate?.ClosedReason is { } closedReason)
        {
            _log.Info("Rejected proactive thread for session {0}: ingress closed", message.SessionId.Value);
            Sender.Tell(new Status.Failure(new InvalidOperationException(closedReason)));
            return;
        }

        // Defense-in-depth: re-validate the channel ACL even though the tool
        // already checked it before posting.
        if (!DiscordAclPolicy.IsAllowedChannel(message.ChannelId, _dependencies.Options, _dependencies.DefaultChannelId))
        {
            _log.Warning("Rejected proactive thread for disallowed channel {0}", message.ChannelId.Value);
            Sender.Tell(new Status.Failure(new InvalidOperationException(
                $"Channel {message.ChannelId.Value} is not in the allowed channels list.")));
            return;
        }

        var sessionBinding = GetOrCreateSessionBinding(
            message.SessionId,
            message.ChannelId,
            message.ReplyChannelId,
            message.ThreadOrMessageId,
            rootMessageId: null);

        _log.Debug("Routing proactive thread setup to session binding {0}", message.SessionId.Value);
        sessionBinding.Forward(message);
    }

    private void HandleTerminated(Terminated msg)
    {
        _log.Debug("Session binding stopped: {0}", msg.ActorRef.Path.Name);
    }

    /// <summary>
    /// Strips the bot mention tag from inbound text and trims whitespace.
    /// </summary>
    private string NormalizeInboundText(string text)
    {
        if (_botMentionTag is null)
            return text.Trim();

        return text.Replace(_botMentionTag, string.Empty, StringComparison.Ordinal).Trim();
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
        var child = Context.ActorOf(props, actorName);
        Context.Watch(child);
        return child;
    }

    private async Task PostIngressClosedReplyAsync(DiscordReplyChannelId replyChannelId, string message)
    {
        try
        {
            await _dependencies.ReplyClient.PostReplyAsync(
                new DiscordPostMessage(replyChannelId, message));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to post restart-drain reply to Discord channel {0}", replyChannelId.Value);
        }
    }

    private static string BuildActorName(DiscordChannelId channelId, DiscordThreadOrMessageId threadOrMessageId)
        => Uri.EscapeDataString($"{channelId.Value}:{threadOrMessageId.Value}");
}
