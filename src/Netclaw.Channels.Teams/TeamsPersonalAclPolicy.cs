// -----------------------------------------------------------------------
// <copyright file="TeamsPersonalAclPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Applies the final default-deny policy for a personal Teams activity.
/// The PR 2 transport context is provisional and must not reach a session.
/// </summary>
public static class TeamsPersonalAclPolicy
{
    public static ChannelAclDecision Evaluate(
        TeamsInboundActivity activity,
        TeamsChannelOptions options)
        => EvaluateCore(activity, options, enforceAllowedUsers: true);

    /// <summary>
    /// Applies all personal-message gates other than the final user/group
    /// principal check. The async principal authorizer performs that check after
    /// this synchronous structural validation succeeds.
    /// </summary>
    public static ChannelAclDecision EvaluateStructuralAccess(
        TeamsInboundActivity activity,
        TeamsChannelOptions options)
        => EvaluateCore(activity, options, enforceAllowedUsers: false);

    private static ChannelAclDecision EvaluateCore(
        TeamsInboundActivity activity,
        TeamsChannelOptions options,
        bool enforceAllowedUsers)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(options);

        if (activity.Trust.Scope != TeamsConversationScope.Personal)
            return ChannelAclDecision.Deny("unsupported_scope");

        if (!options.AllowDirectMessages)
            return ChannelAclDecision.Deny(AclDenyReasons.DirectMessagesDisabled);

        if (string.IsNullOrWhiteSpace(activity.Trust.SenderId))
            return ChannelAclDecision.Deny(AclDenyReasons.MissingUserId);

        if (!TeamsSessionIdentifierCodec.IsValidActivityIdentifier(activity.Trust.ActivityId))
            return ChannelAclDecision.Deny("invalid_activity_id");

        if (string.IsNullOrWhiteSpace(options.TenantId)
            || !string.Equals(activity.Trust.TenantId, options.TenantId, StringComparison.Ordinal))
        {
            return ChannelAclDecision.Deny("configured_tenant_mismatch");
        }

        if (enforceAllowedUsers
            && !options.AllowedUserIds.Contains(activity.Trust.SenderId, StringComparer.Ordinal))
            return ChannelAclDecision.Deny(AclDenyReasons.UserNotAllowed);

        return ChannelAclDecision.Allow(
            TrustAudience.Personal,
            PrincipalClassification.TrustedInternal,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community)
            {
                SourceKind = new SourceKind("teams"),
                SourceScope = new SourceScope("teams-personal")
            });
    }
}
