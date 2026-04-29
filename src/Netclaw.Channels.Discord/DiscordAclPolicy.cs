// -----------------------------------------------------------------------
// <copyright file="DiscordAclPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Actors.Channels;

namespace Netclaw.Channels.Discord;

public static class DiscordAclPolicy
{
    public static ChannelAclDecision EvaluateInbound(
        DiscordGatewayMessage message,
        DiscordChannelOptions options,
        DiscordChannelId? defaultChannelId)
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

        var audienceResult = ResolveAudience(message, options, isExplicitUser, isExplicitChannel);
        if (audienceResult.Error is not null)
            return ChannelAclDecision.Deny(audienceResult.Error);

        var audience = audienceResult.Audience;
        var principal = isExplicitUser
            ? PrincipalClassification.TrustedInternal
            : PrincipalClassification.UntrustedExternal;

        return ChannelAclDecision.Allow(
            audience,
            principal,
            new SourceProvenance
            {
                TransportAuthenticity = TransportAuthenticity.Verified,
                PayloadTaint = PayloadTaint.Public,
                SourceKind = "discord",
                SourceScope = message.ChannelId.Value
            });
    }

    public static bool IsAllowedChannel(
        DiscordChannelId channelId,
        DiscordChannelOptions options,
        DiscordChannelId? defaultChannelId)
    {
        if (defaultChannelId is { } expected
            && string.Equals(channelId.Value, expected.Value, StringComparison.Ordinal))
            return true;

        return options.AllowedChannelIds.Contains(channelId.Value, StringComparer.Ordinal);
    }

    internal static AudienceResult ResolveAudience(
        DiscordGatewayMessage message,
        DiscordChannelOptions options,
        bool isExplicitUser,
        bool isExplicitChannel)
    {
        if (options.ChannelAudiences.TryGetValue(message.ChannelId.Value, out var channelOverride))
        {
            return SecurityPolicyDefaults.TryParseAudience(channelOverride, out var channelAudience)
                ? new AudienceResult(channelAudience)
                : new AudienceResult($"{AclDenyReasons.InvalidChannelAudiencePrefix}:{message.ChannelId.Value}={channelOverride}");
        }

        if (message.IsDirectMessage
            && options.ChannelAudiences.TryGetValue("dm", out var dmOverride))
        {
            return SecurityPolicyDefaults.TryParseAudience(dmOverride, out var dmAudience)
                ? new AudienceResult(dmAudience)
                : new AudienceResult($"{AclDenyReasons.InvalidChannelAudiencePrefix}:dm={dmOverride}");
        }

        var audience = (message.IsDirectMessage || isExplicitUser || isExplicitChannel)
            ? TrustAudience.Team
            : TrustAudience.Public;
        return new AudienceResult(audience);
    }

}
