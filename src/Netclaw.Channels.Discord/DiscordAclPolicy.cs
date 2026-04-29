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
    public static DiscordAclDecision EvaluateInbound(
        DiscordGatewayMessage message,
        DiscordChannelOptions options,
        DiscordChannelId? defaultChannelId)
    {
        if (string.IsNullOrWhiteSpace(message.SenderId.Value))
            return DiscordAclDecision.Deny("missing_user_id");

        if (message.IsDirectMessage && !options.AllowDirectMessages)
            return DiscordAclDecision.Deny("direct_messages_disabled");

        if (!message.IsDirectMessage
            && !IsAllowedChannel(message.ChannelId, options, defaultChannelId))
            return DiscordAclDecision.Deny("channel_not_allowed");

        var isExplicitUser = options.AllowedUserIds.Contains(message.SenderId.Value, StringComparer.Ordinal);
        if (options.AllowedUserIds.Length > 0 && !isExplicitUser)
            return DiscordAclDecision.Deny("user_not_allowed");

        var isExplicitChannel = options.AllowedChannelIds.Contains(message.ChannelId.Value, StringComparer.Ordinal);

        var audienceResult = ResolveAudience(message, options, isExplicitUser, isExplicitChannel);
        if (audienceResult.Error is not null)
            return DiscordAclDecision.Deny(audienceResult.Error);

        var audience = audienceResult.Audience;
        var principal = isExplicitUser
            ? PrincipalClassification.TrustedInternal
            : PrincipalClassification.UntrustedExternal;

        return DiscordAclDecision.Allow(
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
                : new AudienceResult($"invalid_channel_audience:{message.ChannelId.Value}={channelOverride}");
        }

        if (message.IsDirectMessage
            && options.ChannelAudiences.TryGetValue("dm", out var dmOverride))
        {
            return SecurityPolicyDefaults.TryParseAudience(dmOverride, out var dmAudience)
                ? new AudienceResult(dmAudience)
                : new AudienceResult($"invalid_channel_audience:dm={dmOverride}");
        }

        var audience = (message.IsDirectMessage || isExplicitUser || isExplicitChannel)
            ? TrustAudience.Team
            : TrustAudience.Public;
        return new AudienceResult(audience);
    }

}

public sealed record DiscordAclDecision(
    bool IsAllowed,
    string? DenyReason,
    TrustAudience Audience,
    PrincipalClassification Principal,
    SourceProvenance Provenance) : IAclDecision
{
    public static DiscordAclDecision Deny(string reason) => new(
        false,
        reason,
        TrustAudience.Public,
        PrincipalClassification.UntrustedExternal,
        SourceProvenance.StrictDefault());

    public static DiscordAclDecision Allow(
        TrustAudience audience,
        PrincipalClassification principal,
        SourceProvenance provenance) => new(
        true,
        null,
        audience,
        principal,
        provenance);
}
