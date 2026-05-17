// -----------------------------------------------------------------------
// <copyright file="SlackAclPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Shared ACL checks for Slack channel and user authorization.
/// Used by both <see cref="SlackConversationActor"/> (inbound) and
/// <see cref="Tools.SendSlackMessageTool"/> (outbound/proactive).
/// </summary>
public static class SlackAclPolicy
{
    public static ChannelAclDecision EvaluateInbound(
        SlackInboundMessage message,
        SlackChannelOptions options,
        SlackChannelId? defaultChannelId)
    {
        if (message.UserId is not { } userId)
            return ChannelAclDecision.Deny(AclDenyReasons.MissingUserId);

        if (message.IsDirectMessage && !options.AllowDirectMessages)
            return ChannelAclDecision.Deny(AclDenyReasons.DirectMessagesDisabled);

        if (!message.IsDirectMessage
            && !IsAllowedChannel(message.ChannelId, options, defaultChannelId))
            return ChannelAclDecision.Deny(AclDenyReasons.ChannelNotAllowed);

        if (!IsAllowedUser(userId, options))
            return ChannelAclDecision.Deny(AclDenyReasons.UserNotAllowed);

        var isExplicitUser = options.AllowedUserIds.Contains(userId.Value, StringComparer.Ordinal);
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
                SourceKind = new SourceKind("slack"),
                SourceScope = new SourceScope(message.ChannelId.Value)
            });
    }

    /// <summary>
    /// Returns true if <paramref name="channelId"/> is the default channel
    /// or appears in <see cref="SlackChannelOptions.AllowedChannelIds"/>.
    /// </summary>
    public static bool IsAllowedChannel(
        SlackChannelId channelId,
        SlackChannelOptions options,
        SlackChannelId? defaultChannelId)
    {
        if (defaultChannelId is not null && channelId == defaultChannelId.Value)
            return true;

        return options.AllowedChannelIds.Contains(channelId.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns true if the user is permitted. An empty allow-list means all users are allowed.
    /// </summary>
    public static bool IsAllowedUser(SlackUserId userId, SlackChannelOptions options)
    {
        if (options.AllowedUserIds.Length == 0)
            return true;

        return options.AllowedUserIds.Contains(userId.Value, StringComparer.Ordinal);
    }
}
