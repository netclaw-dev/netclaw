// -----------------------------------------------------------------------
// <copyright file="MattermostConversationActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Channels.Mattermost;

/// <summary>
/// Per-channel actor that serves as the security boundary for Mattermost messages.
/// The inbound pipeline (ACL, routing policy, ingress gating, passivation,
/// session binding management) lives in <see cref="ChannelConversationActor{TMessage}"/>;
/// this subclass supplies the Mattermost projections plus the interaction,
/// proactive-thread, and trusted-turn receives.
/// Uses blind-write routing: session IDs are derived deterministically from
/// channel and root post identifiers with no routing state.
/// </summary>
internal sealed class MattermostConversationActor : ChannelConversationActor<MattermostGatewayMessage>
{
    private readonly MattermostChannelId _channelId;
    private readonly MattermostGatewayDependencies _dependencies;
    private readonly string? _botMentionTag;

    public MattermostConversationActor(MattermostChannelId channelId, MattermostGatewayDependencies dependencies)
        : base(ChannelType.Mattermost, channelId.Value, dependencies.IngressGate)
    {
        _channelId = channelId;
        _dependencies = dependencies;
        _botMentionTag = !string.IsNullOrEmpty(dependencies.BotUsername) ? $"@{dependencies.BotUsername}" : null;

        Receive<MattermostGatewayInteraction>(HandleGatewayInteraction);
        Receive<StartMattermostProactiveThread>(HandleProactiveThread);
        Receive<DeliverTrustedSessionTurn>(HandleTrustedSessionTurn);
    }

    public static Props CreateProps(MattermostChannelId channelId, MattermostGatewayDependencies dependencies)
        => Props.Create(() => new MattermostConversationActor(channelId, dependencies));

    protected override string ThreadLogContextKey => "MattermostRootPostId";

    protected override string EventLogContextKey => "MattermostEventId";

    protected override ChannelAclDecision EvaluateAcl(MattermostGatewayMessage message) =>
        MattermostAclPolicy.EvaluateInbound(
            message,
            _dependencies.Options,
            _dependencies.DefaultChannelId);

    protected override bool IsBotMessage(MattermostGatewayMessage message) => message.IsBotMessage;

    protected override string EventIdOf(MattermostGatewayMessage message) => message.EventId.Value;

    protected override string ThreadKeyOf(MattermostGatewayMessage message) =>
        SessionRootIdOf(message).Value;

    protected override string TextOf(MattermostGatewayMessage message) => message.Text;

    protected override bool HasAttachments(MattermostGatewayMessage message) =>
        message.Attachments is { Count: > 0 };

    protected override ChannelRoutingVerdict EvaluateRouting(MattermostGatewayMessage message, bool threadExists)
    {
        var decision = MattermostRoutingPolicy.Evaluate(
            message,
            _dependencies.Options.MentionOnly,
            _dependencies.Options.AllowDirectMessages,
            _dependencies.Options.MentionRequiredInDm,
            threadExists,
            message.ContainsBotMention);

        return decision.Kind switch
        {
            MattermostRoutingDecisionKind.Ignore => ChannelRoutingVerdict.Ignore(
                // decision.IgnoreReason is non-null here by MattermostRoutingDecision
                // factory invariant (Ignore-kind is only constructed via Ignore(reason)).
                decision.IgnoreReason!.Value.ToString(),
                MattermostRoutingDecision.TelemetryLabelFor(decision.IgnoreReason!.Value)),
            MattermostRoutingDecisionKind.ContinueOnly => ChannelRoutingVerdict.ContinueOnly,
            _ => ChannelRoutingVerdict.StartOrContinue,
        };
    }

    protected override Props CreateSessionBindingProps(SessionId sessionId, MattermostGatewayMessage message) =>
        SessionBindingProps(sessionId, _channelId, SessionRootIdOf(message));

    protected override object CreateThreadInbound(
        SessionId sessionId,
        MattermostGatewayMessage message,
        ChannelAclDecision aclDecision,
        string normalizedText) =>
        new MattermostThreadInbound(
            SessionId: sessionId,
            ChannelId: _channelId,
            PostId: message.PostId,
            RootPostId: SessionRootIdOf(message),
            EventId: message.EventId,
            SenderId: message.SenderId,
            Audience: aclDecision.Audience,
            Principal: aclDecision.Principal,
            Provenance: aclDecision.Provenance,
            Text: normalizedText,
            ReceivedAt: message.ReceivedAt,
            Attachments: message.Attachments);

    protected override async Task PostIngressClosedReplyAsync(MattermostGatewayMessage message, string closedReason)
    {
        try
        {
            await _dependencies.ReplyClient.PostReplyAsync(
                new MattermostPostMessage(message.ChannelId, closedReason, message.PostId));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to post restart-drain reply to Mattermost channel {0}", message.ChannelId.Value);
        }
    }

    protected override string NormalizeInboundText(string text)
    {
        if (_botMentionTag is null)
            return text.Trim();

        if (text.Contains(_botMentionTag, StringComparison.OrdinalIgnoreCase))
            text = text.Replace(_botMentionTag, string.Empty, StringComparison.OrdinalIgnoreCase);

        return text.Trim();
    }

    private void HandleGatewayInteraction(MattermostGatewayInteraction interaction)
    {
        if (!MattermostAclPolicy.IsAllowedUser(interaction.SenderId, _dependencies.Options))
        {
            Log.Info(
                "interaction_denied sender={0} reason=user_not_allowed",
                interaction.SenderId.Value);
            Telemetry.RecordEventDropped("interaction_user_not_allowed");
            return;
        }

        var sessionId = SessionIdFormat.Build(_channelId.Value, interaction.RootPostId.Value);
        var sessionBinding = GetOrCreateSessionBinding(
            _channelId.Value,
            interaction.RootPostId.Value,
            () => SessionBindingProps(sessionId, _channelId, interaction.RootPostId));

        Telemetry.RecordEventRouted("interaction");
        sessionBinding.Forward(new MattermostApprovalResponse(
            ChannelId: _channelId,
            RootPostId: interaction.RootPostId,
            CallId: new ToolCallId(interaction.CallId),
            SelectedKey: interaction.SelectedKey,
            SenderId: interaction.SenderId,
            RequesterSenderId: interaction.RequesterSenderId,
            PromptPostId: interaction.PromptPostId));
    }

    private void HandleProactiveThread(StartMattermostProactiveThread message)
    {
        // Defense-in-depth: re-validate the ACL even though the tool already
        // checked it before posting. DM channel IDs are ephemeral transport
        // channels, so the configured user ACL is the authority for DMs.
        if (message.DirectMessageUserId is { } dmUserId)
        {
            if (!_dependencies.Options.AllowDirectMessages)
            {
                Log.Warning(
                    "Rejecting proactive DM for user {User}: direct messages disabled",
                    dmUserId.Value);
                Sender.Tell(CommandNack.For(
                    message.SessionId,
                    "Mattermost direct messages are disabled"));
                return;
            }

            if (!MattermostAclPolicy.IsAllowedUser(dmUserId, _dependencies.Options))
            {
                Log.Warning(
                    "Rejecting proactive DM for disallowed user {User}",
                    dmUserId.Value);
                Sender.Tell(CommandNack.For(
                    message.SessionId,
                    $"User {dmUserId.Value} is not in the allowed users list"));
                return;
            }
        }
        else if (!MattermostAclPolicy.IsAllowedChannel(
                message.ChannelId,
                _dependencies.Options,
                _dependencies.DefaultChannelId))
        {
            Log.Warning(
                "Rejecting proactive thread for disallowed channel {Channel}",
                message.ChannelId.Value);
            Sender.Tell(CommandNack.For(
                message.SessionId,
                $"Channel {message.ChannelId.Value} is not in the allowed channels list"));
            return;
        }

        GetOrCreateSessionBinding(
            message.ChannelId.Value,
            message.RootPostId.Value,
            () => SessionBindingProps(message.SessionId, message.ChannelId, message.RootPostId));

        Log.Info(
            "proactive_thread session={Session} channel={Channel} rootPost={RootPost}",
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
            NackUnparseableSessionId(message);
            return;
        }

        if (parsedChannelId != _channelId)
        {
            NackConversationMismatch(message);
            return;
        }

        var sessionBinding = GetOrCreateSessionBinding(
            _channelId.Value,
            rootPostId.Value,
            () => SessionBindingProps(message.SessionId, _channelId, rootPostId));

        Log.Debug(
            "Routing DeliverTrustedSessionTurn session={Session} channel={Channel} rootPost={RootPost}",
            message.SessionId.Value, parsedChannelId.Value, rootPostId.Value);
        sessionBinding.Forward(message);
    }

    /// <summary>
    /// The session root is the message's root post, falling back to the post
    /// itself when the message starts a new thread.
    /// </summary>
    private static MattermostRootPostId SessionRootIdOf(MattermostGatewayMessage message) =>
        message.RootPostId.IsEmpty
            ? new MattermostRootPostId(message.PostId.Value)
            : message.RootPostId;

    private Props SessionBindingProps(
        SessionId sessionId,
        MattermostChannelId channelId,
        MattermostRootPostId rootPostId) =>
        _dependencies.SessionPropsFactory?.Invoke(sessionId, channelId, rootPostId, _dependencies)
            ?? MattermostSessionBindingActor.CreateProps(sessionId, channelId, rootPostId, _dependencies);
}
