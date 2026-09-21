// -----------------------------------------------------------------------
// <copyright file="TeamsPrincipalAuthorizationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TeamsPrincipalAuthorizationTests
{
    [Fact]
    public async Task Explicit_user_bypasses_group_directory_lookup()
    {
        var directory = new FakeTeamsDirectory();
        var authorizer = new TeamsPrincipalAuthorizer(new TeamsChannelOptions
        {
            AllowedUserIds = ["user-a"],
            AllowedGroupIds = ["group-a"]
        }, directory);

        var result = await authorizer.AuthorizeAsync(CreateChannelActivity(), TestContext.Current.CancellationToken);

        Assert.True(result.IsAllowed);
        Assert.Equal(PrincipalClassification.TrustedInternal, result.Principal);
        Assert.Equal(0, directory.MembershipCalls);
    }

    [Fact]
    public async Task Group_membership_authorizes_and_returns_trusted_internal()
    {
        var directory = new FakeTeamsDirectory(new HashSet<string>(["group-a"], StringComparer.Ordinal));
        var authorizer = new TeamsPrincipalAuthorizer(new TeamsChannelOptions
        {
            AllowedGroupIds = ["group-a"]
        }, directory);

        var result = await authorizer.AuthorizeAsync(CreateChannelActivity(), TestContext.Current.CancellationToken);

        Assert.True(result.IsAllowed);
        Assert.Equal(PrincipalClassification.TrustedInternal, result.Principal);
        Assert.Equal(1, directory.MembershipCalls);
        Assert.Equal(["group-a"], directory.LastGroupIds);
    }

    [Fact]
    public async Task Membership_unavailability_fails_closed_with_stable_reason()
    {
        var directory = new FakeTeamsDirectory(status: TeamsDirectoryOperationStatus.Unavailable);
        var authorizer = new TeamsPrincipalAuthorizer(new TeamsChannelOptions
        {
            AllowedGroupIds = ["group-a"]
        }, directory);

        var result = await authorizer.AuthorizeAsync(CreateChannelActivity(), TestContext.Current.CancellationToken);

        Assert.False(result.IsAllowed);
        Assert.Equal("teams_group_membership_unavailable", result.ReasonCode);
    }

    [Fact]
    public async Task Channel_specific_principals_are_unioned_with_global_principals()
    {
        var directory = new FakeTeamsDirectory(new HashSet<string>(["channel-group"], StringComparer.Ordinal));
        var authorizer = new TeamsPrincipalAuthorizer(new TeamsChannelOptions
        {
            AllowedGroupIds = ["global-group"],
            ChannelAccessOverrides =
            [
                new TeamsChannelAccessOverride
                {
                    TeamId = "team-a",
                    ChannelId = "channel-a",
                    AllowedGroupIds = ["channel-group"]
                }
            ]
        }, directory);

        var result = await authorizer.AuthorizeAsync(CreateChannelActivity(), TestContext.Current.CancellationToken);

        Assert.True(result.IsAllowed);
        Assert.Equal(["channel-group", "global-group"], directory.LastGroupIds.OrderBy(static id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Channel_without_principal_restrictions_preserves_legacy_untrusted_access()
    {
        var directory = new FakeTeamsDirectory();
        var authorizer = new TeamsPrincipalAuthorizer(new TeamsChannelOptions(), directory);

        var result = await authorizer.AuthorizeAsync(CreateChannelActivity(), TestContext.Current.CancellationToken);

        Assert.True(result.IsAllowed);
        Assert.Equal(PrincipalClassification.UntrustedExternal, result.Principal);
        Assert.Equal(0, directory.MembershipCalls);
    }

    [Fact]
    public async Task Direct_messages_require_a_global_user_or_group()
    {
        var noPrincipalAuthorizer = new TeamsPrincipalAuthorizer(new TeamsChannelOptions(), new FakeTeamsDirectory());
        var groupAuthorizer = new TeamsPrincipalAuthorizer(
            new TeamsChannelOptions { AllowedGroupIds = ["group-a"] },
            new FakeTeamsDirectory(new HashSet<string>(["group-a"], StringComparer.Ordinal)));

        var denied = await noPrincipalAuthorizer.AuthorizeAsync(CreatePersonalActivity(), TestContext.Current.CancellationToken);
        var allowed = await groupAuthorizer.AuthorizeAsync(CreatePersonalActivity(), TestContext.Current.CancellationToken);

        Assert.False(denied.IsAllowed);
        Assert.Equal("teams_group_membership_not_allowed", denied.ReasonCode);
        Assert.True(allowed.IsAllowed);
    }

    [Fact]
    public async Task Group_chats_require_a_global_user_or_verified_group_member()
    {
        var deniedAuthorizer = new TeamsPrincipalAuthorizer(new TeamsChannelOptions(), new FakeTeamsDirectory());
        var memberAuthorizer = new TeamsPrincipalAuthorizer(
            new TeamsChannelOptions { AllowedGroupIds = ["group-a"] },
            new FakeTeamsDirectory(new HashSet<string>(["group-a"], StringComparer.Ordinal)));

        var denied = await deniedAuthorizer.AuthorizeAsync(CreateGroupChatActivity(), TestContext.Current.CancellationToken);
        var allowed = await memberAuthorizer.AuthorizeAsync(CreateGroupChatActivity(), TestContext.Current.CancellationToken);

        Assert.False(denied.IsAllowed);
        Assert.Equal("teams_group_membership_not_allowed", denied.ReasonCode);
        Assert.True(allowed.IsAllowed);
        Assert.Equal(PrincipalClassification.TrustedInternal, allowed.Principal);
    }

    [Fact]
    public async Task Group_chats_ignore_channel_specific_principals()
    {
        var directory = new FakeTeamsDirectory(new HashSet<string>(["channel-group"], StringComparer.Ordinal));
        var authorizer = new TeamsPrincipalAuthorizer(new TeamsChannelOptions
        {
            ChannelAccessOverrides =
            [
                new TeamsChannelAccessOverride
                {
                    TeamId = "team-a",
                    ChannelId = "channel-a",
                    AllowedGroupIds = ["channel-group"]
                }
            ]
        }, directory);

        var result = await authorizer.AuthorizeAsync(CreateGroupChatActivity(), TestContext.Current.CancellationToken);

        Assert.False(result.IsAllowed);
        Assert.Equal("teams_group_membership_not_allowed", result.ReasonCode);
        Assert.Equal(0, directory.MembershipCalls);
    }

    private static TeamsInboundActivity CreateChannelActivity() => new(
        CreateTrust(TeamsConversationScope.Channel),
        "prompt",
        new TeamsReplyMetadata(null, "root-a"),
        isMentioned: true,
        teamId: "team-a",
        channelId: "channel-a");

    private static TeamsInboundActivity CreatePersonalActivity() => new(
        CreateTrust(TeamsConversationScope.Personal),
        "prompt");

    private static TeamsInboundActivity CreateGroupChatActivity() => new(
        CreateTrust(TeamsConversationScope.GroupChat),
        "prompt",
        isMentioned: true);

    private static TeamsIngressTrustContext CreateTrust(TeamsConversationScope scope) => new(
        TrustAudience.Public,
        PrincipalClassification.UntrustedExternal,
        scope switch
        {
            TeamsConversationScope.Personal => TrustBoundary.Personal,
            TeamsConversationScope.GroupChat => TrustBoundary.Team,
            _ => TrustBoundary.Public
        },
        new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
        "user-a",
        "tenant-a",
        scope switch
        {
            TeamsConversationScope.Personal => "conversation-a",
            TeamsConversationScope.GroupChat => "19:group-chat@thread.v2",
            _ => "conversation-a;messageid=root-a"
        },
        scope,
        "activity-a",
        DateTimeOffset.UnixEpoch);

    private sealed class FakeTeamsDirectory(
        IReadOnlySet<string>? membership = null,
        TeamsDirectoryOperationStatus status = TeamsDirectoryOperationStatus.Available) : ITeamsDirectory
    {
        private readonly IReadOnlySet<string> _membership = membership ?? new HashSet<string>(StringComparer.Ordinal);

        public int MembershipCalls { get; private set; }

        public IReadOnlyCollection<string> LastGroupIds { get; private set; } = [];

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>> SearchTeamsAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryTeam>> GetTeamAsync(
            string teamId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryTeam>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>> GetChannelsAsync(
            string teamId,
            int maximumResults,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryChannel>> GetChannelAsync(
            string teamId,
            string channelId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryChannel>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>> SearchUsersAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>> SearchGroupsAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroup>> GetGroupAsync(
            string groupId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryGroup>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryUser>> GetUserAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryUser>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlySet<string>>> CheckUserGroupMembershipAsync(
            string userId,
            IReadOnlyCollection<string> groupIds,
            CancellationToken cancellationToken = default)
        {
            MembershipCalls++;
            LastGroupIds = [.. groupIds];
            return ValueTask.FromResult(status == TeamsDirectoryOperationStatus.Available
                ? TeamsDirectoryOperationResult<IReadOnlySet<string>>.Available(_membership)
                : TeamsDirectoryOperationResult<IReadOnlySet<string>>.Unavailable("safe_unavailable"));
        }
    }
}
