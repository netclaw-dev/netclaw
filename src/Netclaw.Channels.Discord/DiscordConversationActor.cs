// -----------------------------------------------------------------------
// <copyright file="DiscordConversationActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Discord;

/// <summary>
/// Per-channel actor that serves as the security boundary for Discord messages.
/// The inbound pipeline (ACL, routing policy, ingress gating, passivation,
/// session binding management) lives in <see cref="ChannelConversationActor{TMessage}"/>;
/// this subclass supplies the Discord projections plus the interaction,
/// proactive-thread, and trusted-turn receives.
/// Uses blind-write routing: session IDs are derived deterministically from
/// message identifiers with no routing state.
/// </summary>
internal sealed class DiscordConversationActor : ChannelConversationActor<DiscordGatewayMessage>
{
    private readonly DiscordChannelId _channelId;
    private readonly DiscordGatewayDependencies _dependencies;
    private readonly string? _botMentionTag;

    public DiscordConversationActor(DiscordChannelId channelId, DiscordGatewayDependencies dependencies)
        : base(ChannelType.Discord, channelId.Value, dependencies.IngressGate)
    {
        _channelId = channelId;
        _dependencies = dependencies;
        _botMentionTag = dependencies.BotUserId is { } botId ? $"<@{botId.Value}>" : null;

        Receive<DiscordGatewayInteraction>(HandleGatewayInteraction);
        Receive<DeliverTrustedSessionTurn>(HandleTrustedSessionTurn);
        Receive<StartProactiveThread>(HandleProactiveThread);
    }

    public static Props CreateProps(DiscordChannelId channelId, DiscordGatewayDependencies dependencies)
        => Props.Create(() => new DiscordConversationActor(channelId, dependencies));

    protected override string ThreadLogContextKey => "DiscordThreadOrMessageId";

    protected override string EventLogContextKey => "DiscordEventId";

    protected override ChannelAclDecision EvaluateAcl(DiscordGatewayMessage message) =>
        DiscordAclPolicy.EvaluateInbound(
            message,
            _dependencies.Options,
            _dependencies.DefaultChannelId);

    protected override bool IsBotMessage(DiscordGatewayMessage message) => message.IsBotMessage;

    protected override string EventIdOf(DiscordGatewayMessage message) => message.EventId.Value;

    protected override string ThreadKeyOf(DiscordGatewayMessage message) => message.ThreadOrMessageId.Value;

    protected override string TextOf(DiscordGatewayMessage message) => message.Text;

    protected override bool HasAttachments(DiscordGatewayMessage message) =>
        message.Attachments is { Count: > 0 };

    protected override ChannelRoutingVerdict EvaluateRouting(DiscordGatewayMessage message, bool threadExists)
    {
        var decision = DiscordRoutingPolicy.Evaluate(
            message,
            _dependencies.Options.MentionOnly,
            _dependencies.Options.AllowDirectMessages,
            _dependencies.Options.MentionRequiredInDm,
            _dependencies.Options.MentionRequiredInThread,
            threadExists,
            message.ContainsBotMention);

        return decision.Kind switch
        {
            DiscordRoutingDecisionKind.Ignore => ChannelRoutingVerdict.Ignore(
                // decision.IgnoreReason is non-null here by DiscordRoutingDecision
                // factory invariant (Ignore-kind is only constructed via Ignore(reason)).
                decision.IgnoreReason!.Value.ToString(),
                DiscordRoutingDecision.TelemetryLabelFor(decision.IgnoreReason!.Value)),
            DiscordRoutingDecisionKind.ContinueOnly => ChannelRoutingVerdict.ContinueOnly,
            _ => ChannelRoutingVerdict.StartOrContinue,
        };
    }

    protected override Props CreateSessionBindingProps(SessionId sessionId, DiscordGatewayMessage message) =>
        SessionBindingProps(
            sessionId,
            _channelId,
            message.ReplyChannelId,
            message.ThreadOrMessageId,
            message.RootMessageId);

    protected override object CreateThreadInbound(
        SessionId sessionId,
        DiscordGatewayMessage message,
        ChannelAclDecision aclDecision,
        string normalizedText) =>
        new DiscordThreadInbound(
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
            Attachments: message.Attachments);

    protected override async Task PostIngressClosedReplyAsync(DiscordGatewayMessage message, string closedReason)
    {
        try
        {
            await _dependencies.ReplyClient.PostReplyAsync(
                new DiscordPostMessage(message.ReplyChannelId, closedReason));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to post restart-drain reply to Discord channel {0}", message.ReplyChannelId.Value);
        }
    }

    /// <summary>
    /// Strips the bot mention tag from inbound text and trims whitespace.
    /// </summary>
    protected override string NormalizeInboundText(string text)
    {
        if (_botMentionTag is null)
            return text.Trim();

        return text.Replace(_botMentionTag, string.Empty, StringComparison.Ordinal).Trim();
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
        var sessionId = SessionIdFormat.Build(_channelId.Value, interaction.ThreadOrMessageId.Value);
        var sessionBinding = GetOrCreateSessionBinding(
            _channelId.Value,
            interaction.ThreadOrMessageId.Value,
            () => SessionBindingProps(
                sessionId,
                _channelId,
                replyChannelId,
                interaction.ThreadOrMessageId,
                rootMessageId: null));

        Telemetry.RecordEventRouted("interaction");
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
            NackUnparseableSessionId(message);
            return;
        }

        if (parsedChannelId != _channelId)
        {
            NackConversationMismatch(message);
            return;
        }

        var replyChannelId = new DiscordReplyChannelId(threadOrMessageId.Value);
        var sessionBinding = GetOrCreateSessionBinding(
            _channelId.Value,
            threadOrMessageId.Value,
            () => SessionBindingProps(
                message.SessionId,
                _channelId,
                replyChannelId,
                threadOrMessageId,
                rootMessageId: null));

        Log.Debug(
            "Routing DeliverTrustedSessionTurn session={Session} channel={Channel} threadOrMessage={ThreadOrMessage}",
            message.SessionId.Value, parsedChannelId.Value, threadOrMessageId.Value);
        sessionBinding.Forward(message);
    }

    private void HandleProactiveThread(StartProactiveThread message)
    {
        if (_dependencies.IngressGate?.ClosedReason is { } closedReason)
        {
            Log.Info("Rejected proactive thread for session {0}: ingress closed", message.SessionId.Value);
            Sender.Tell(new Status.Failure(new InvalidOperationException(closedReason)));
            return;
        }

        // Defense-in-depth: re-validate the ACL even though the tool already
        // checked it before posting. DM channel IDs are ephemeral transport
        // channels, so the configured user ACL is the authority for DMs.
        if (message.DirectMessageUserId is { } dmUserId)
        {
            if (!_dependencies.Options.AllowDirectMessages)
            {
                Log.Warning("Rejected proactive DM for user {0}: direct messages disabled", dmUserId.Value);
                Sender.Tell(new Status.Failure(new InvalidOperationException(
                    "Discord direct messages are disabled.")));
                return;
            }

            if (!DiscordAclPolicy.IsAllowedUser(dmUserId, _dependencies.Options))
            {
                Log.Warning("Rejected proactive DM for disallowed user {0}", dmUserId.Value);
                Sender.Tell(new Status.Failure(new InvalidOperationException(
                    $"User {dmUserId.Value} is not in the allowed users list.")));
                return;
            }
        }
        else if (!DiscordAclPolicy.IsAllowedChannel(message.ChannelId, _dependencies.Options, _dependencies.DefaultChannelId))
        {
            Log.Warning("Rejected proactive thread for disallowed channel {0}", message.ChannelId.Value);
            Sender.Tell(new Status.Failure(new InvalidOperationException(
                $"Channel {message.ChannelId.Value} is not in the allowed channels list.")));
            return;
        }

        var sessionBinding = GetOrCreateSessionBinding(
            message.ChannelId.Value,
            message.ThreadOrMessageId.Value,
            () => SessionBindingProps(
                message.SessionId,
                message.ChannelId,
                message.ReplyChannelId,
                message.ThreadOrMessageId,
                message.RootMessageId));

        Log.Debug("Routing proactive thread setup to session binding {0}", message.SessionId.Value);
        sessionBinding.Forward(message);
    }

    private Props SessionBindingProps(
        SessionId sessionId,
        DiscordChannelId channelId,
        DiscordReplyChannelId replyChannelId,
        DiscordThreadOrMessageId threadOrMessageId,
        DiscordMessageId? rootMessageId) =>
        _dependencies.SessionPropsFactory?.Invoke(
            sessionId, channelId, replyChannelId, threadOrMessageId, rootMessageId, _dependencies)
        ?? DiscordSessionBindingActor.CreateProps(
            sessionId, channelId, replyChannelId, threadOrMessageId, rootMessageId, _dependencies);
}
