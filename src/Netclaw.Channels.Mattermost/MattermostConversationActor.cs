// -----------------------------------------------------------------------
// <copyright file="MattermostConversationActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Channels.Mattermost;

/// <summary>
/// Per-channel actor that serves as the security boundary for Mattermost messages.
/// Performs ACL checks, routing policy evaluation, and ingress gating.
/// Uses blind-write routing: session IDs are derived deterministically from
/// channel and root post identifiers with no routing state.
/// </summary>
internal sealed class MattermostConversationActor : ReceiveActor
{
    private const int MaxInboundTextLength = 4000;

    private readonly MattermostChannelId _channelId;
    private readonly MattermostGatewayDependencies _dependencies;
    private readonly string? _botMentionTag;
    private readonly ILoggingAdapter _log;

    public MattermostConversationActor(MattermostChannelId channelId, MattermostGatewayDependencies dependencies)
    {
        _channelId = channelId;
        _dependencies = dependencies;
        _botMentionTag = !string.IsNullOrEmpty(dependencies.BotUsername) ? $"@{dependencies.BotUsername}" : null;
        _log = Context.GetLogger()
            .WithContext("Adapter", "mattermost")
            .WithContext("MattermostChannelId", _channelId.Value);

        Context.SetReceiveTimeout(TimeSpan.FromHours(2));

        Receive<ReceiveTimeout>(_ =>
        {
            _log.Info("Mattermost conversation idle for 2 hours, passivating");
            Context.Stop(Self);
        });

        Receive<MattermostGatewayMessage>(HandleGatewayMessage);
        Receive<MattermostGatewayInteraction>(HandleGatewayInteraction);
        Receive<StartMattermostProactiveThread>(HandleProactiveThread);
        Receive<DeliverTrustedSessionTurn>(HandleTrustedSessionTurn);
        Receive<Terminated>(HandleTerminated);
    }

    protected override SupervisorStrategy SupervisorStrategy()
        => new OneForOneStrategy(ex =>
        {
            _log.Error(ex, "Session binding child failed; stopping to allow re-creation");
            return Directive.Stop;
        });

    public static Props CreateProps(MattermostChannelId channelId, MattermostGatewayDependencies dependencies)
        => Props.Create(() => new MattermostConversationActor(channelId, dependencies));

    private void HandleGatewayMessage(MattermostGatewayMessage message)
    {
        var options = _dependencies.Options;

        var aclDecision = MattermostAclPolicy.EvaluateInbound(
            message,
            options,
            _dependencies.DefaultChannelId);

        if (!aclDecision.IsAllowed)
        {
            var reason = aclDecision.DenyReason ?? "acl_denied";
            _log.Info("mattermost_event_dropped event={0} reason={1}", message.EventId.Value, reason);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventDropped(reason);
            return;
        }

        if (message.IsBotMessage)
        {
            _log.Info("mattermost_event_filtered event={0} reason=bot_message", message.EventId.Value);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventFiltered("bot_message");
            return;
        }

        if (_dependencies.IngressGate?.ClosedReason is { } closedReason)
        {
            _log.Info("mattermost_event_filtered event={0} reason=restart_drain_active", message.EventId.Value);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventFiltered("restart_drain_active");
            _ = PostIngressClosedReplyAsync(message.ChannelId, message.PostId, closedReason);
            return;
        }

        var sessionRootId = message.RootPostId.IsEmpty
            ? new MattermostRootPostId(message.PostId.Value)
            : message.RootPostId;

        var actorName = BuildActorName(_channelId, sessionRootId);
        var existingBinding = Context.Child(actorName);
        var threadExists = !existingBinding.IsNobody();

        var decision = MattermostRoutingPolicy.Evaluate(
            message,
            options.MentionOnly,
            options.AllowDirectMessages,
            options.MentionRequiredInDm,
            threadExists,
            message.ContainsBotMention);

        if (decision.Kind is MattermostRoutingDecisionKind.Ignore)
        {
            var ignoreReason = decision.IgnoreReason!.Value;
            _log.Info(
                "mattermost_event_filtered event={0} reason=routing_policy_ignore ignoreReason={1}",
                message.EventId.Value,
                ignoreReason);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventFiltered(
                MattermostRoutingDecision.TelemetryLabelFor(ignoreReason));
            return;
        }

        if (decision.Kind is MattermostRoutingDecisionKind.ContinueOnly && !threadExists)
        {
            _log.Info("mattermost_event_dropped event={0} reason=thread_not_initialized", message.EventId.Value);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventDropped("thread_not_initialized");
            return;
        }

        var normalizedText = NormalizeInboundText(message.Text);
        if (normalizedText.Length > MaxInboundTextLength)
        {
            _log.Warning("mattermost_inbound_text_truncated original={OriginalLength} clamped={MaxLength}",
                normalizedText.Length, MaxInboundTextLength);
            normalizedText = normalizedText[..MaxInboundTextLength];
        }
        var hasAttachments = message.Attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(normalizedText) && !hasAttachments)
        {
            _log.Info("mattermost_event_filtered event={0} reason=empty_text", message.EventId.Value);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventFiltered("empty_text");
            return;
        }

        var sessionId = new SessionId($"{_channelId.Value}/{sessionRootId.Value}");
        var sessionBinding = threadExists
            ? existingBinding
            : GetOrCreateSessionBinding(sessionId, _channelId, sessionRootId);

        var turnId = string.IsNullOrWhiteSpace(message.EventId.Value)
            ? IdGen.ShortId()
            : message.EventId.Value;

        var log = _log
            .WithContext("MattermostRootPostId", sessionRootId.Value)
            .WithContext("SessionId", sessionId.Value)
            .WithContext("TurnId", turnId)
            .WithContext("MattermostEventId", message.EventId.Value);

        log.Info("mattermost_turn_routed event={EventId} textChars={TextLength}",
            message.EventId.Value,
            normalizedText.Length);

        ChannelTelemetry.For(ChannelType.Mattermost).RecordEventRouted("message");
        sessionBinding.Forward(new MattermostThreadInbound(
            SessionId: sessionId,
            ChannelId: _channelId,
            PostId: message.PostId,
            RootPostId: sessionRootId,
            EventId: message.EventId,
            SenderId: message.SenderId,
            Audience: aclDecision.Audience,
            Principal: aclDecision.Principal,
            Provenance: aclDecision.Provenance,
            Text: normalizedText,
            ReceivedAt: message.ReceivedAt,
            Attachments: message.Attachments));
    }

    private void HandleGatewayInteraction(MattermostGatewayInteraction interaction)
    {
        if (!MattermostAclPolicy.IsAllowedUser(interaction.SenderId, _dependencies.Options))
        {
            _log.Info(
                "mattermost_interaction_denied sender={0} reason=user_not_allowed",
                interaction.SenderId.Value);
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventDropped("interaction_user_not_allowed");
            return;
        }

        var sessionId = new SessionId($"{_channelId.Value}/{interaction.RootPostId.Value}");
        var sessionBinding = GetOrCreateSessionBinding(
            sessionId,
            _channelId,
            interaction.RootPostId);

        ChannelTelemetry.For(ChannelType.Mattermost).RecordEventRouted("interaction");
        sessionBinding.Forward(new MattermostApprovalResponse(
            ChannelId: _channelId,
            RootPostId: interaction.RootPostId,
            CallId: new ToolCallId(interaction.CallId),
            SelectedKey: interaction.SelectedKey,
            SenderId: interaction.SenderId,
            RequesterSenderId: interaction.RequesterSenderId));
    }

    private void HandleProactiveThread(StartMattermostProactiveThread message)
    {
        if (!MattermostAclPolicy.IsAllowedChannel(
                message.ChannelId,
                _dependencies.Options,
                _dependencies.DefaultChannelId))
        {
            _log.Warning(
                "Rejecting proactive thread for disallowed channel {Channel}",
                message.ChannelId.Value);
            Sender.Tell(CommandNack.For(
                message.SessionId,
                $"Channel {message.ChannelId.Value} is not in the allowed channels list"));
            return;
        }

        var sessionBinding = GetOrCreateSessionBinding(
            message.SessionId,
            message.ChannelId,
            message.RootPostId);

        _log.Info(
            "mattermost_proactive_thread session={Session} channel={Channel} rootPost={RootPost}",
            message.SessionId.Value, message.ChannelId.Value, message.RootPostId.Value);
        Sender.Tell(new MattermostProactiveThreadAck(message.SessionId));
    }

    private void HandleTrustedSessionTurn(DeliverTrustedSessionTurn message)
    {
        if (!MattermostGatewayActor.TryParseMattermostSessionId(
                message.SessionId,
                out var parsedChannelId,
                out var rootPostId))
        {
            _log.Warning(
                "Dropping DeliverTrustedSessionTurn with unparseable Mattermost SessionId {SessionId}",
                message.SessionId.Value);
            Sender.Tell(CommandNack.For(message.SessionId, "Invalid Mattermost SessionId format"));
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
            rootPostId);

        _log.Debug(
            "Routing DeliverTrustedSessionTurn session={Session} channel={Channel} rootPost={RootPost}",
            message.SessionId.Value, parsedChannelId.Value, rootPostId.Value);
        sessionBinding.Forward(message);
    }

    private void HandleTerminated(Terminated msg)
    {
        _log.Debug("Session binding stopped: {0}", msg.ActorRef.Path.Name);
    }

    private string NormalizeInboundText(string text)
    {
        if (_botMentionTag is null)
            return text.Trim();

        if (text.Contains(_botMentionTag, StringComparison.OrdinalIgnoreCase))
            text = text.Replace(_botMentionTag, string.Empty, StringComparison.OrdinalIgnoreCase);

        return text.Trim();
    }

    private IActorRef GetOrCreateSessionBinding(
        SessionId sessionId,
        MattermostChannelId channelId,
        MattermostRootPostId rootPostId)
    {
        var actorName = BuildActorName(channelId, rootPostId);
        var existing = Context.Child(actorName);
        if (!existing.IsNobody())
            return existing;

        var props = _dependencies.SessionPropsFactory?.Invoke(
                        sessionId, channelId, rootPostId, _dependencies)
                    ?? MattermostSessionBindingActor.CreateProps(
                        sessionId, channelId, rootPostId, _dependencies);
        var child = Context.ActorOf(props, actorName);
        Context.Watch(child);
        return child;
    }

    private async Task PostIngressClosedReplyAsync(MattermostChannelId channelId, MattermostPostId rootPostId, string message)
    {
        try
        {
            await _dependencies.ReplyClient.PostReplyAsync(
                new MattermostPostMessage(channelId, message, rootPostId));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to post restart-drain reply to Mattermost channel {0}", channelId.Value);
        }
    }

    private static string BuildActorName(MattermostChannelId channelId, MattermostRootPostId rootPostId)
        => Uri.EscapeDataString($"{channelId.Value}:{rootPostId.Value}");
}
