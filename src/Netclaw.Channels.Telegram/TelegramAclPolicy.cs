// -----------------------------------------------------------------------
// <copyright file="TelegramAclPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Telegram;

/// <summary>
/// Shared access checks for Telegram chats and users.
/// </summary>
public static class TelegramAclPolicy
{
    public static ChannelAclDecision EvaluateInbound(TelegramInboundMessage message, TelegramChannelOptions options)
    {
        if (message.UserId is not { } userId)
            return ChannelAclDecision.Deny(AclDenyReasons.MissingUserId);

        if (message.IsDirectMessage && !options.AllowDirectMessages)
            return ChannelAclDecision.Deny(AclDenyReasons.DirectMessagesDisabled);

        if (!message.IsDirectMessage && !IsAllowedChat(message.ChatId, options))
            return ChannelAclDecision.Deny(AclDenyReasons.ChannelNotAllowed);

        if (!IsAllowedUser(userId, options))
            return ChannelAclDecision.Deny(AclDenyReasons.UserNotAllowed);

        var chatId = message.ChatId.ToString();
        var userIdText = userId.ToString();
        var isExplicitUser = options.AllowedUserIds.Contains(userIdText, StringComparer.Ordinal);
        var isExplicitChat = options.AllowedChatIds.Contains(chatId, StringComparer.Ordinal);
        var audienceResult = AudienceResult.Resolve(
            chatId, message.IsDirectMessage, options.ChatAudiences, isExplicitUser, isExplicitChat);

        if (audienceResult.Error is not null)
            return ChannelAclDecision.Deny(audienceResult.Error);

        return ChannelAclDecision.Allow(
            audienceResult.Audience,
            isExplicitUser ? PrincipalClassification.TrustedInternal : PrincipalClassification.UntrustedExternal,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new SourceKind("telegram"),
                SourceScope = new SourceScope(chatId)
            });
    }

    public static bool IsAllowedChat(long chatId, TelegramChannelOptions options) =>
        options.AllowedChatIds.Contains(chatId.ToString(), StringComparer.Ordinal);

    public static bool IsAllowedUser(long userId, TelegramChannelOptions options) =>
        options.AllowedUserIds.Length == 0
        || options.AllowedUserIds.Contains(userId.ToString(), StringComparer.Ordinal);
}
