// -----------------------------------------------------------------------
// <copyright file="TelegramInboundMessage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Telegram;

using Akka.Actor;
using Netclaw.Actors.Protocol;

public sealed record TelegramInboundMessage(
    long ChatId,
    long? UserId,
    int MessageId,
    string Text,
    bool IsDirectMessage,
    IReadOnlyList<TelegramFileReference>? Files = null,
    bool ContainsBotMention = false,
    bool IsReplyToBot = false);

public sealed record TelegramFileReference(
    string FileId,
    string Name,
    string MimeType,
    long Size);

public sealed record TelegramCallbackQuery(
    long ChatId,
    long UserId,
    int MessageId,
    string QueryId,
    string Data);

public sealed record TelegramApprovalButton(string Label, string CallbackData);

public sealed record StartTelegramProactiveChat(
    TelegramChatId ChatId,
    SessionId SessionId,
    bool IsDirectMessage) : INoSerializationVerificationNeeded;

public sealed record TelegramProactiveChatAck(SessionId SessionId) : INoSerializationVerificationNeeded;
