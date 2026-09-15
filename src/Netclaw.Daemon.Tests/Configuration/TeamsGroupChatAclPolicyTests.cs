// -----------------------------------------------------------------------
// <copyright file="TeamsGroupChatAclPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TeamsGroupChatAclPolicyTests
{
    [Fact]
    public void Disabled_group_chats_fail_closed()
    {
        var decision = TeamsGroupChatAclPolicy.EvaluateStructuralAccess(
            CreateActivity(),
            CreateOptions(allowGroupChats: false));

        Assert.False(decision.IsAllowed);
        Assert.Equal("group_chats_disabled", decision.DenyReason);
    }

    [Fact]
    public void Unknown_group_chat_fails_closed()
    {
        var decision = TeamsGroupChatAclPolicy.EvaluateStructuralAccess(
            CreateActivity(),
            CreateOptions(allowedGroupChatIds: ["19:other@thread.v2"]));

        Assert.False(decision.IsAllowed);
        Assert.Equal("group_chat_not_allowed", decision.DenyReason);
    }

    [Fact]
    public void Noncanonical_group_chat_fails_closed()
    {
        var decision = TeamsGroupChatAclPolicy.EvaluateStructuralAccess(
            CreateActivity(conversationId: "group-chat-name"),
            CreateOptions(allowedGroupChatIds: ["group-chat-name"]));

        Assert.False(decision.IsAllowed);
        Assert.Equal("invalid_group_chat_id", decision.DenyReason);
    }

    [Fact]
    public void Allowed_group_chat_and_user_receive_team_audience()
    {
        var decision = TeamsGroupChatAclPolicy.Evaluate(
            CreateActivity(),
            CreateOptions(allowedUserIds: ["user-a"]));

        Assert.True(decision.IsAllowed);
        Assert.Equal(TrustAudience.Team, decision.Audience);
        Assert.Equal(PrincipalClassification.TrustedInternal, decision.Principal);
        Assert.Equal("teams-groupchat", decision.Provenance.SourceScope!.Value.Value);
    }

    [Fact]
    public void Mention_only_group_chat_rejects_each_unmentioned_message()
    {
        var decision = TeamsGroupChatAclPolicy.EvaluateStructuralAccess(
            CreateActivity(isMentioned: false),
            CreateOptions());

        Assert.False(decision.IsAllowed);
        Assert.Equal("group_chat_unmentioned", decision.DenyReason);
    }

    private static TeamsChannelOptions CreateOptions(
        string[]? allowedGroupChatIds = null,
        string[]? allowedUserIds = null,
        bool allowGroupChats = true) => new()
    {
        TenantId = "tenant-a",
        AllowGroupChats = allowGroupChats,
        MentionOnly = true,
        AllowedGroupChatIds = allowedGroupChatIds ?? ["19:group-chat@thread.v2"],
        AllowedUserIds = allowedUserIds ?? []
    };

    private static TeamsInboundActivity CreateActivity(
        bool isMentioned = true,
        string conversationId = "19:group-chat@thread.v2") => new(
        new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "user-a",
            "tenant-a",
            conversationId,
            TeamsConversationScope.GroupChat,
            "activity-a",
            DateTimeOffset.UnixEpoch),
        "prompt",
        new TeamsReplyMetadata(null, null, "https://service.invalid/"),
        isMentioned);
}
