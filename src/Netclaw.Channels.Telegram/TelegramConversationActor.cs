// -----------------------------------------------------------------------
// <copyright file="TelegramConversationActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Telegram;

internal sealed class TelegramConversationActor : ChannelConversationActor<TelegramInboundMessage>
{
    private readonly TelegramChatId _chatId;
    private readonly TelegramGatewayDependencies _dependencies;

    public TelegramConversationActor(TelegramChatId chatId, TelegramGatewayDependencies dependencies)
        : base(ChannelType.Telegram, chatId.Value.ToString(), dependencies.IngressGate)
    {
        _chatId = chatId;
        _dependencies = dependencies;

        Receive<TelegramCallbackQuery>(callback =>
        {
            var sessionId = BuildSessionId("chat");
            var binding = GetOrCreateSessionBinding(
                _chatId.Value.ToString(),
                "chat",
                () => TelegramSessionBindingActor.CreateProps(sessionId, _chatId, _dependencies));
            binding.Forward(callback);
        });

        Receive<StartTelegramProactiveChat>(HandleProactiveChat);
        Receive<DeliverTrustedSessionTurn>(HandleTrustedSessionTurn);
    }

    public static Props CreateProps(TelegramChatId chatId, TelegramGatewayDependencies dependencies) =>
        Props.Create(() => new TelegramConversationActor(chatId, dependencies));

    protected override string ThreadLogContextKey => "TelegramChatId";

    protected override string EventLogContextKey => "TelegramMessageId";

    protected override ChannelAclDecision EvaluateAcl(TelegramInboundMessage message) =>
        TelegramAclPolicy.EvaluateInbound(message, _dependencies.Options);

    protected override bool IsBotMessage(TelegramInboundMessage message) => false;

    protected override string EventIdOf(TelegramInboundMessage message) =>
        $"{message.ChatId}:{message.MessageId}";

    protected override string ThreadKeyOf(TelegramInboundMessage message) => "chat";

    protected override string TextOf(TelegramInboundMessage message) => message.Text;

    protected override bool HasAttachments(TelegramInboundMessage message) =>
        message.Files is { Count: > 0 };

    protected override string NormalizeInboundText(string text) => text.Trim();

    protected override ChannelRoutingVerdict EvaluateRouting(
        TelegramInboundMessage message,
        bool threadExists)
    {
        var decision = TelegramRoutingPolicy.Evaluate(message, _dependencies.Options.MentionOnly);
        return decision.Kind switch
        {
            TelegramRoutingDecisionKind.Ignore => ChannelRoutingVerdict.Ignore(
                decision.IgnoreReason!.Value.ToString(),
                TelegramRoutingDecision.TelemetryLabelFor(decision.IgnoreReason.Value)),
            _ => ChannelRoutingVerdict.StartOrContinue
        };
    }

    protected override Props CreateSessionBindingProps(SessionId sessionId, TelegramInboundMessage message) =>
        TelegramSessionBindingActor.CreateProps(sessionId, _chatId, _dependencies);

    protected override object CreateThreadInbound(
        SessionId sessionId,
        TelegramInboundMessage message,
        ChannelAclDecision aclDecision,
        string normalizedText) =>
        new TelegramSessionInbound(sessionId, message, aclDecision, normalizedText);

    protected override async Task PostIngressClosedReplyAsync(
        TelegramInboundMessage message,
        string closedReason)
    {
        try
        {
            await _dependencies.Transport.SendTextAsync(message.ChatId, closedReason);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to post restart-drain reply to Telegram chat {0}", message.ChatId);
        }
    }

    private void HandleProactiveChat(StartTelegramProactiveChat message)
    {
        if (_dependencies.IngressGate?.ClosedReason is { } closedReason)
        {
            Sender.Tell(new Status.Failure(new InvalidOperationException(closedReason)));
            return;
        }

        var allowed = message.IsDirectMessage
            ? _dependencies.Options.AllowDirectMessages
              && _dependencies.Options.AllowedUserIds.Contains(
                  message.ChatId.Value.ToString(), StringComparer.Ordinal)
            : TelegramAclPolicy.IsAllowedChat(message.ChatId.Value, _dependencies.Options);
        if (!allowed)
        {
            Sender.Tell(new Status.Failure(new InvalidOperationException(
                $"Telegram chat {message.ChatId.Value} is not allowed.")));
            return;
        }

        var binding = GetOrCreateSessionBinding(
            _chatId.Value.ToString(),
            "chat",
            () => TelegramSessionBindingActor.CreateProps(message.SessionId, _chatId, _dependencies));
        binding.Forward(message);
    }

    private void HandleTrustedSessionTurn(DeliverTrustedSessionTurn message)
    {
        if (!TelegramGatewayActor.TryParseTelegramSessionId(message.SessionId, out var parsedChatId))
        {
            NackUnparseableSessionId(message);
            return;
        }

        if (parsedChatId != _chatId)
        {
            NackConversationMismatch(message);
            return;
        }

        var binding = GetOrCreateSessionBinding(
            _chatId.Value.ToString(),
            "chat",
            () => TelegramSessionBindingActor.CreateProps(message.SessionId, _chatId, _dependencies));
        binding.Forward(message);
    }
}

internal sealed record TelegramSessionInbound(
    SessionId SessionId,
    TelegramInboundMessage Message,
    ChannelAclDecision AclDecision,
    string Text);
