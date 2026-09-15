// -----------------------------------------------------------------------
// <copyright file="TeamsGroupChatAclPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Applies the default-deny policy for a flat Microsoft Teams group chat.
/// A configured chat is never proof that a sender is authorized.
/// </summary>
public static class TeamsGroupChatAclPolicy
{
    public static ChannelAclDecision Evaluate(TeamsInboundActivity activity, TeamsChannelOptions options)
        => EvaluateCore(activity, options, enforcePrincipal: true);

    /// <summary>
    /// Applies the non-principal gates before the asynchronous global-principal
    /// authorization check. Mention-only remains a structural group-chat gate.
    /// </summary>
    public static ChannelAclDecision EvaluateStructuralAccess(TeamsInboundActivity activity, TeamsChannelOptions options)
        => EvaluateCore(activity, options, enforcePrincipal: false);

    private static ChannelAclDecision EvaluateCore(
        TeamsInboundActivity activity,
        TeamsChannelOptions options,
        bool enforcePrincipal)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(options);

        if (activity.Trust.Scope != TeamsConversationScope.GroupChat)
            return ChannelAclDecision.Deny("unsupported_scope");
        if (!options.AllowGroupChats)
            return ChannelAclDecision.Deny("group_chats_disabled");
        if (string.IsNullOrWhiteSpace(options.TenantId)
            || !string.Equals(activity.Trust.TenantId, options.TenantId, StringComparison.Ordinal))
        {
            return ChannelAclDecision.Deny("configured_tenant_mismatch");
        }

        if (!TeamsSessionIdentifierCodec.IsCanonicalGroupChatConversationId(activity.Trust.ConversationId))
            return ChannelAclDecision.Deny("invalid_group_chat_id");
        if (options.AllowedGroupChatIds.Length == 0
            || !options.AllowedGroupChatIds.Contains(activity.Trust.ConversationId, StringComparer.Ordinal))
        {
            return ChannelAclDecision.Deny("group_chat_not_allowed");
        }

        if (string.IsNullOrWhiteSpace(activity.Trust.SenderId))
            return ChannelAclDecision.Deny(AclDenyReasons.MissingUserId);
        if (!TeamsSessionIdentifierCodec.IsValidActivityIdentifier(activity.Trust.ActivityId))
            return ChannelAclDecision.Deny("invalid_activity_id");
        if (activity.Kind == TeamsIngressActivityKind.Message
            && options.MentionOnly
            && !activity.IsMentioned)
        {
            return ChannelAclDecision.Deny("group_chat_unmentioned");
        }

        if (enforcePrincipal
            && !options.AllowedUserIds.Contains(activity.Trust.SenderId, StringComparer.Ordinal))
        {
            return ChannelAclDecision.Deny(AclDenyReasons.UserNotAllowed);
        }

        return ChannelAclDecision.Allow(
            TrustAudience.Team,
            options.AllowedUserIds.Contains(activity.Trust.SenderId, StringComparer.Ordinal)
                ? PrincipalClassification.TrustedInternal
                : PrincipalClassification.UntrustedExternal,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community)
            {
                SourceKind = new SourceKind("teams"),
                SourceScope = new SourceScope("teams-groupchat")
            });
    }
}
