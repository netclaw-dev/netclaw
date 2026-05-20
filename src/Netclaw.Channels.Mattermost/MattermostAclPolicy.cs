// -----------------------------------------------------------------------
// <copyright file="MattermostAclPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Actors.Channels;

namespace Netclaw.Channels.Mattermost;

public static class MattermostAclPolicy
{
    public static ChannelAclDecision EvaluateInbound(
        MattermostGatewayMessage message,
        MattermostChannelOptions options,
        MattermostChannelId? defaultChannelId)
    {
        if (string.IsNullOrWhiteSpace(message.SenderId.Value))
            return ChannelAclDecision.Deny(AclDenyReasons.MissingUserId);

        if (message.IsDirectMessage && !options.AllowDirectMessages)
            return ChannelAclDecision.Deny(AclDenyReasons.DirectMessagesDisabled);

        if (!message.IsDirectMessage
            && !IsAllowedChannel(message.ChannelId, options, defaultChannelId))
            return ChannelAclDecision.Deny(AclDenyReasons.ChannelNotAllowed);

        var isExplicitUser = options.AllowedUserIds.Contains(message.SenderId.Value, StringComparer.Ordinal);
        if (options.AllowedUserIds.Length > 0 && !isExplicitUser)
            return ChannelAclDecision.Deny(AclDenyReasons.UserNotAllowed);

        var isExplicitChannel = options.AllowedChannelIds.Contains(message.ChannelId.Value, StringComparer.Ordinal);

        var audienceResult = AudienceResult.Resolve(
            message.ChannelId.Value, message.IsDirectMessage,
            options.ChannelAudiences, isExplicitUser, isExplicitChannel);
        if (audienceResult.Error is not null)
            return ChannelAclDecision.Deny(audienceResult.Error);

        var audience = audienceResult.Audience;
        var principal = isExplicitUser
            ? PrincipalClassification.TrustedInternal
            : PrincipalClassification.UntrustedExternal;

        return ChannelAclDecision.Allow(
            audience,
            principal,
            new SourceProvenance(
                TransportAuthenticity.Verified,
                PayloadTaint.Public)
            {
                SourceKind = new SourceKind("mattermost"),
                SourceScope = new SourceScope(message.ChannelId.Value)
            });
    }

    public static bool IsAllowedChannel(
        MattermostChannelId channelId,
        MattermostChannelOptions options,
        MattermostChannelId? defaultChannelId)
    {
        if (defaultChannelId is { } expected
            && string.Equals(channelId.Value, expected.Value, StringComparison.Ordinal))
            return true;

        return options.AllowedChannelIds.Contains(channelId.Value, StringComparer.Ordinal);
    }

    public static bool IsAllowedUser(
        MattermostUserId userId,
        MattermostChannelOptions options)
    {
        if (options.AllowedUserIds.Length == 0)
            return true;

        return options.AllowedUserIds.Contains(userId.Value, StringComparer.Ordinal);
    }
}
