// -----------------------------------------------------------------------
// <copyright file="TeamsPrincipalAuthorization.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Teams;

/// <summary>
/// The canonical principal restrictions that apply to one Teams activity.
/// </summary>
public sealed record TeamsPrincipalRequirement(
    IReadOnlySet<string> AllowedUserIds,
    IReadOnlySet<string> AllowedGroupIds)
{
    public bool HasRestriction => AllowedUserIds.Count > 0 || AllowedGroupIds.Count > 0;
}

/// <summary>
/// A stable, non-secret Teams principal authorization outcome.
/// </summary>
public sealed record TeamsPrincipalAuthorizationDecision(
    bool IsAllowed,
    string ReasonCode,
    PrincipalClassification Principal)
{
    public static TeamsPrincipalAuthorizationDecision Allow(PrincipalClassification principal) =>
        new(true, "teams_principal_allowed", principal);

    public static TeamsPrincipalAuthorizationDecision Deny(string reasonCode) =>
        new(false, reasonCode, PrincipalClassification.UntrustedExternal);
}

/// <summary>
/// An activity-bound authorization result produced by the asynchronous ingress
/// edge. It is an in-memory handoff only and is never persisted or accepted for
/// a different Teams activity.
/// </summary>
public sealed record TeamsIngressAuthorization(
    string SenderId,
    string TenantId,
    string ConversationId,
    string ActivityId,
    TeamsConversationScope Scope,
    ChannelAclDecision Acl)
{
    public static TeamsIngressAuthorization Create(TeamsInboundActivity activity, ChannelAclDecision acl) =>
        new(
            activity.Trust.SenderId,
            activity.Trust.TenantId,
            activity.Trust.ConversationId,
            activity.Trust.ActivityId,
            activity.Trust.Scope,
            acl);

    public bool AppliesTo(TeamsInboundActivity activity) =>
        string.Equals(SenderId, activity.Trust.SenderId, StringComparison.Ordinal)
        && string.Equals(TenantId, activity.Trust.TenantId, StringComparison.Ordinal)
        && string.Equals(ConversationId, activity.Trust.ConversationId, StringComparison.Ordinal)
        && string.Equals(ActivityId, activity.Trust.ActivityId, StringComparison.Ordinal)
        && Scope == activity.Trust.Scope;
}

/// <summary>
/// Resolves the global and exact per-channel principal configuration applicable
/// to a Teams activity. Canonical IDs are the authority; labels never enter this
/// calculation.
/// </summary>
public static class TeamsPrincipalRequirements
{
    public static TeamsPrincipalRequirement Resolve(TeamsInboundActivity activity, TeamsChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(options);

        var users = new HashSet<string>(options.AllowedUserIds.Where(IsCanonicalValue), StringComparer.Ordinal);
        var groups = new HashSet<string>(options.AllowedGroupIds.Where(IsCanonicalValue), StringComparer.Ordinal);

        if (activity.Trust.Scope == TeamsConversationScope.Channel)
        {
            foreach (var accessOverride in options.ChannelAccessOverrides)
            {
                if (!string.Equals(accessOverride.TeamId, activity.TeamId, StringComparison.Ordinal)
                    || !string.Equals(accessOverride.ChannelId, activity.ChannelId, StringComparison.Ordinal))
                {
                    continue;
                }

                users.UnionWith(accessOverride.AllowedUserIds.Where(IsCanonicalValue));
                groups.UnionWith(accessOverride.AllowedGroupIds.Where(IsCanonicalValue));
            }
        }

        return new TeamsPrincipalRequirement(users, groups);
    }

    private static bool IsCanonicalValue(string? value) => !string.IsNullOrWhiteSpace(value);
}

/// <summary>
/// Performs the final user/group authorization after the synchronous structural
/// Teams ACL has accepted an activity. Explicit users bypass Graph; applicable
/// group restrictions fail closed on any unavailable directory result.
/// </summary>
public sealed class TeamsPrincipalAuthorizer(TeamsChannelOptions options, ITeamsDirectory? directory)
{
    public async ValueTask<TeamsPrincipalAuthorizationDecision> AuthorizeAsync(
        TeamsInboundActivity activity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (string.IsNullOrWhiteSpace(activity.Trust.SenderId))
            return TeamsPrincipalAuthorizationDecision.Deny("teams_group_membership_not_allowed");

        var requirement = TeamsPrincipalRequirements.Resolve(activity, options);
        if (requirement.AllowedUserIds.Contains(activity.Trust.SenderId))
            return TeamsPrincipalAuthorizationDecision.Allow(PrincipalClassification.TrustedInternal);

        if (!requirement.HasRestriction)
        {
            return activity.Trust.Scope == TeamsConversationScope.Channel
                ? TeamsPrincipalAuthorizationDecision.Allow(PrincipalClassification.UntrustedExternal)
                : TeamsPrincipalAuthorizationDecision.Deny("teams_group_membership_not_allowed");
        }

        if (requirement.AllowedGroupIds.Count == 0 || directory is null)
            return TeamsPrincipalAuthorizationDecision.Deny("teams_group_membership_unavailable");

        TeamsDirectoryOperationResult<IReadOnlySet<string>> membership;
        try
        {
            membership = await directory.CheckUserGroupMembershipAsync(
                activity.Trust.SenderId,
                requirement.AllowedGroupIds,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return TeamsPrincipalAuthorizationDecision.Deny("teams_group_membership_unavailable");
        }

        if (!membership.IsAvailable || membership.Value is null)
            return TeamsPrincipalAuthorizationDecision.Deny("teams_group_membership_unavailable");

        return membership.Value.Count > 0
            ? TeamsPrincipalAuthorizationDecision.Allow(PrincipalClassification.TrustedInternal)
            : TeamsPrincipalAuthorizationDecision.Deny("teams_group_membership_not_allowed");
    }
}

/// <summary>
/// Rechecks structural ACL conditions in actors while accepting an activity-bound
/// edge decision for new group or channel-specific principal restrictions. This
/// keeps Graph I/O out of Akka dispatchers and preserves the legacy policy path
/// when the additive configuration is unused.
/// </summary>
public static class TeamsActorAclEvaluator
{
    public static ChannelAclDecision? Evaluate(TeamsInboundActivity activity, TeamsChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(options);

        if (activity.Trust.Scope == TeamsConversationScope.Personal)
        {
            var structural = TeamsPersonalAclPolicy.EvaluateStructuralAccess(activity, options);
            if (!structural.IsAllowed)
                return null;

            if (!RequiresStampedDecision(activity, options))
            {
                var legacy = TeamsPersonalAclPolicy.Evaluate(activity, options);
                return legacy.IsAllowed ? legacy : null;
            }

            return ResolveStampedAcl(activity, structural);
        }

        if (activity.Trust.Scope == TeamsConversationScope.GroupChat)
        {
            var structural = TeamsGroupChatAclPolicy.EvaluateStructuralAccess(activity, options);
            if (!structural.IsAllowed)
                return null;

            return ResolveStampedAcl(activity, structural);
        }

        var channel = TeamsChannelAclPolicy.EvaluateStructuralAccess(activity, options);
        if (channel.Disposition != TeamsChannelPolicyDisposition.Allowed || channel.Acl is null)
            return null;

        if (!RequiresStampedDecision(activity, options))
        {
            var legacy = TeamsChannelAclPolicy.EvaluateAccess(activity, options);
            return legacy.Disposition == TeamsChannelPolicyDisposition.Allowed ? legacy.Acl : null;
        }

        return ResolveStampedAcl(activity, channel.Acl);
    }

    private static bool RequiresStampedDecision(TeamsInboundActivity activity, TeamsChannelOptions options)
    {
        if (activity.Trust.Scope == TeamsConversationScope.GroupChat)
            return true;

        if (options.AllowedGroupIds.Length > 0)
            return true;

        return activity.Trust.Scope == TeamsConversationScope.Channel
               && options.ChannelAccessOverrides.Any(accessOverride =>
                   string.Equals(accessOverride.TeamId, activity.TeamId, StringComparison.Ordinal)
                   && string.Equals(accessOverride.ChannelId, activity.ChannelId, StringComparison.Ordinal)
                   && (accessOverride.AllowedUserIds.Length > 0 || accessOverride.AllowedGroupIds.Length > 0));
    }

    private static ChannelAclDecision? ResolveStampedAcl(TeamsInboundActivity activity, ChannelAclDecision structural)
    {
        var authorization = activity.Authorization;
        if (authorization is null
            || !authorization.AppliesTo(activity)
            || !authorization.Acl.IsAllowed
            || authorization.Acl.Audience != structural.Audience
            || authorization.Acl.Provenance != structural.Provenance)
        {
            return null;
        }

        return authorization.Acl;
    }
}
