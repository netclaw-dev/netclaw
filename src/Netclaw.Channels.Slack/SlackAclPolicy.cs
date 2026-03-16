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
    public static SlackAclDecision EvaluateInbound(
        SlackInboundMessage message,
        SlackChannelOptions options,
        SlackChannelId? defaultChannelId)
    {
        if (message.UserId is not { } userId)
            return SlackAclDecision.Deny("missing_user_id");

        var isConversationAllowed = message.IsDirectMessage
            ? options.AllowDirectMessages
            : IsAllowedChannel(message.ChannelId, options, defaultChannelId);

        if (!isConversationAllowed)
            return SlackAclDecision.Deny("channel_not_allowed");

        if (!IsAllowedUser(userId, options))
            return SlackAclDecision.Deny("user_not_allowed");

        var isExplicitUser = options.AllowedUserIds.Contains(userId.Value, StringComparer.Ordinal);
        var isExplicitChannel = options.AllowedChannelIds.Contains(message.ChannelId.Value, StringComparer.Ordinal);
        var audience = (message.IsDirectMessage || isExplicitUser || isExplicitChannel)
            ? TrustAudience.Team
            : TrustAudience.Public;

        var principal = isExplicitUser
            ? PrincipalClassification.TrustedInternal
            : PrincipalClassification.UntrustedExternal;

        return SlackAclDecision.Allow(
            audience,
            principal,
            new SourceProvenance
            {
                TransportAuthenticity = TransportAuthenticity.Verified,
                PayloadTaint = PayloadTaint.Public,
                SourceKind = "slack",
                SourceScope = message.ChannelId.Value
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

public sealed record SlackAclDecision(
    bool IsAllowed,
    string? DenyReason,
    TrustAudience Audience,
    PrincipalClassification Principal,
    SourceProvenance Provenance)
{
    public static SlackAclDecision Deny(string reason) => new(
        false,
        reason,
        TrustAudience.Public,
        PrincipalClassification.UntrustedExternal,
        SourceProvenance.StrictDefault());

    public static SlackAclDecision Allow(
        TrustAudience audience,
        PrincipalClassification principal,
        SourceProvenance provenance) => new(
        true,
        null,
        audience,
        principal,
        provenance);
}
