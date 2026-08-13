// -----------------------------------------------------------------------
// <copyright file="TelegramGatewayActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Telegram;

public sealed class TelegramGatewayActor : ChannelGatewayActor<TelegramChatId>
{
    private readonly TelegramGatewayDependencies _dependencies;

    public TelegramGatewayActor(TelegramGatewayDependencies dependencies)
        : base(ChannelType.Telegram)
    {
        _dependencies = dependencies;

        Receive<TelegramInboundMessage>(message =>
        {
            ChannelTelemetry.For(ChannelType.Telegram).RecordEventReceived("message");

            var eventId = $"{message.ChatId}:{message.MessageId}";
            if (!TryMarkEventProcessed(eventId))
            {
                Log.Debug("Dropping duplicate Telegram message {0}", eventId);
                ChannelTelemetry.For(ChannelType.Telegram).RecordEventFiltered("duplicate_event");
                return;
            }

            var chatId = new TelegramChatId(message.ChatId);
            var conversation = GetOrCreateConversation(chatId);
            Log.Debug("Routing Telegram message {0} to chat {1}", eventId, chatId);
            ChannelTelemetry.For(ChannelType.Telegram).RecordEventRouted("message");
            conversation.Forward(message);
        });

        Receive<TelegramCallbackQuery>(callback =>
        {
            ChannelTelemetry.For(ChannelType.Telegram).RecordEventReceived("interaction");
            var conversation = GetOrCreateConversation(new TelegramChatId(callback.ChatId));
            conversation.Forward(callback);
        });

        Receive<StartTelegramProactiveChat>(message =>
        {
            var conversation = GetOrCreateConversation(message.ChatId);
            conversation.Forward(message);
        });
    }

    public static Props CreateProps(TelegramGatewayDependencies dependencies) =>
        Props.Create(() => new TelegramGatewayActor(dependencies));

    internal static bool TryParseTelegramSessionId(SessionId sessionId, out TelegramChatId chatId)
    {
        chatId = default;
        if (!SessionIdFormat.TrySplit(sessionId, out var chatPart, out _)
            || !long.TryParse(chatPart, out var value))
            return false;

        chatId = new TelegramChatId(value);
        return true;
    }

    protected override string ChannelIdValue(TelegramChatId channelId) => channelId.Value.ToString();

    protected override Props CreateConversationProps(TelegramChatId channelId) =>
        _dependencies.ConversationPropsFactory?.Invoke(channelId, _dependencies)
        ?? TelegramConversationActor.CreateProps(channelId, _dependencies);

    protected override bool TryParseSessionChannelId(SessionId sessionId, out TelegramChatId channelId) =>
        TryParseTelegramSessionId(sessionId, out channelId);
}

public sealed record TelegramGatewayDependencies(
    ISessionPipeline Pipeline,
    SessionIngressGate? IngressGate,
    TelegramChannelOptions Options,
    TelegramTransport Transport,
    IContentScanner ContentScanner,
    ToolAudienceProfiles AudienceProfiles,
    ModelCapabilities ModelCapabilities,
    NetclawPaths Paths,
    IChannelRegistry? ChannelRegistry = null,
    Func<TelegramChatId, TelegramGatewayDependencies, Props>? ConversationPropsFactory = null);
