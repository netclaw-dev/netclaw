// -----------------------------------------------------------------------
// <copyright file="TeamsChannelRoutingPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TeamsChannelRoutingPolicyTests
{
    [Theory]
    [InlineData("conversation")]
    [InlineData("conversation;messageid=")]
    [InlineData("conversation;messageid=root;other=value")]
    [InlineData("conversation;messageid=root;messageid=other")]
    [InlineData("conversation;messageid=rootmessageid=other")]
    public void Channel_root_parser_rejects_unproven_or_ambiguous_shapes(string conversationId)
    {
        Assert.False(TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(conversationId, out _));
    }

    [Fact]
    public void Channel_root_parser_preserves_the_opaque_tenant_proven_suffix()
    {
        Assert.True(TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(
            "conversation;messageid=ACTIVITY_ROOT_001", out var root));
        Assert.Equal("ACTIVITY_ROOT_001", root);
    }

    [Fact]
    public void Channel_root_parser_rejects_an_oversized_suffix()
    {
        var conversationId = "conversation;messageid=" + new string('a', TeamsSessionIdentifierCodec.MaxRawIdentifierBytes + 1);

        Assert.False(TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(conversationId, out _));
    }

    [Fact]
    public void Mention_removal_preserves_literal_text_for_a_malformed_entity_span()
    {
        var text = "bot says hello";

        var normalized = TeamsTenantEvidenceMappings.RemoveQualifiedBotMentions(
            text,
            [new TeamsMentionEvidence("mention", "28:bot", "bot")],
            "28:bot",
            "bot");

        Assert.Equal(text, normalized);
    }

    [Fact]
    public void Channel_acl_requires_team_channel_and_optional_user_access()
    {
        var options = AllowedOptions();

        Assert.Equal(TeamsChannelPolicyDisposition.Allowed, TeamsChannelAclPolicy.Evaluate(CreateActivity(), options).Disposition);
        Assert.Equal(TeamsChannelPolicyDisposition.Denied, TeamsChannelAclPolicy.Evaluate(CreateActivity(teamId: "team-other"), options).Disposition);
        Assert.Equal(TeamsChannelPolicyDisposition.Denied, TeamsChannelAclPolicy.Evaluate(CreateActivity(channelId: "channel-other"), options).Disposition);
        Assert.Equal(TeamsChannelPolicyDisposition.Denied, TeamsChannelAclPolicy.Evaluate(
            CreateActivity(), CreateOptions(allowedUserIds: ["user-other"])).Disposition);
        Assert.Equal(TeamsChannelPolicyDisposition.Denied, TeamsChannelAclPolicy.Evaluate(
            CreateActivity(), CreateOptions(allowedTeamIds: [])).Disposition);
        Assert.Equal(TeamsChannelPolicyDisposition.Denied, TeamsChannelAclPolicy.Evaluate(
            CreateActivity(), CreateOptions(allowedChannelIds: [])).Disposition);
    }

    [Fact]
    public void Mention_only_ignores_allowed_unmentioned_traffic_without_downgrading_acl()
    {
        var unmentioned = CreateActivity(isMentioned: false);

        Assert.Equal(TeamsChannelPolicyDisposition.Ignored, TeamsChannelAclPolicy.Evaluate(unmentioned, AllowedOptions()).Disposition);
        Assert.Equal(TeamsChannelPolicyDisposition.Allowed, TeamsChannelAclPolicy.EvaluateAccess(unmentioned, AllowedOptions()).Disposition);
        Assert.Equal(TeamsChannelPolicyDisposition.Allowed, TeamsChannelAclPolicy.Evaluate(
            unmentioned, CreateOptions(mentionOnly: false)).Disposition);
    }

    [Fact]
    public void Channel_mutations_require_the_same_identity_acl_as_messages()
    {
        var update = CreateActivity(teamId: "team-other", kind: TeamsIngressActivityKind.MessageUpdate);

        Assert.Equal(TeamsChannelPolicyDisposition.Denied, TeamsChannelAclPolicy.Evaluate(update, AllowedOptions()).Disposition);
    }

    [Fact]
    public void Channel_audience_uses_team_channel_then_team_then_public_fallback()
    {
        var activity = CreateActivity();

        Assert.Equal(TrustAudience.Public, TeamsChannelAclPolicy.Evaluate(activity, AllowedOptions()).Acl!.Audience);
        Assert.Equal(TrustAudience.Team, TeamsChannelAclPolicy.Evaluate(activity, CreateOptions(
            channelAudiences: new Dictionary<string, string> { ["team-a"] = "team" })).Acl!.Audience);
        Assert.Equal(TrustAudience.Personal, TeamsChannelAclPolicy.Evaluate(activity, CreateOptions(
            channelAudiences: new Dictionary<string, string> { ["team-a/channel-a"] = "personal", ["team-a"] = "public" })).Acl!.Audience);
    }

    [Fact]
    public void Structured_channel_audience_override_supports_delimiter_bearing_canonical_ids()
    {
        const string teamId = "19:team-id@thread.tacv2";
        const string channelId = "19:channel-id@thread.tacv2";
        var options = CreateOptions(
            allowedTeamIds: [teamId],
            allowedChannelIds: [channelId],
            channelAudienceOverrides:
            [
                new TeamsChannelAudienceOverride
                {
                    TeamId = teamId,
                    ChannelId = channelId,
                    Audience = "team"
                }
            ]);

        var decision = TeamsChannelAclPolicy.Evaluate(
            CreateActivity(teamId: teamId, channelId: channelId),
            options);

        Assert.Equal(TeamsChannelPolicyDisposition.Allowed, decision.Disposition);
        Assert.Equal(TrustAudience.Team, decision.Acl!.Audience);
    }

    [Fact]
    public void Structured_channel_audience_override_prefers_exact_channel_and_rejects_duplicates()
    {
        var activity = CreateActivity();
        var exact = new TeamsChannelAudienceOverride
        {
            TeamId = "team-a",
            ChannelId = "channel-a",
            Audience = "team"
        };
        var teamWide = new TeamsChannelAudienceOverride
        {
            TeamId = "team-a",
            Audience = "public"
        };

        var resolved = TeamsChannelAclPolicy.Evaluate(
            activity,
            CreateOptions(channelAudienceOverrides: [teamWide, exact]));
        var duplicate = TeamsChannelAclPolicy.Evaluate(
            activity,
            CreateOptions(channelAudienceOverrides: [exact, exact]));

        Assert.Equal(TrustAudience.Team, resolved.Acl!.Audience);
        Assert.Equal(TeamsChannelPolicyDisposition.Denied, duplicate.Disposition);
        Assert.Equal("invalid_channel_audience", duplicate.ReasonCode);
    }

    private static TeamsChannelOptions AllowedOptions() => CreateOptions();

    private static TeamsChannelOptions CreateOptions(
        string[]? allowedTeamIds = null,
        string[]? allowedChannelIds = null,
        string[]? allowedUserIds = null,
        bool mentionOnly = true,
        Dictionary<string, string>? channelAudiences = null,
        TeamsChannelAudienceOverride[]? channelAudienceOverrides = null) => new()
    {
        TenantId = "tenant-a",
        MentionOnly = mentionOnly,
        AllowedTeamIds = allowedTeamIds ?? ["team-a"],
        AllowedChannelIds = allowedChannelIds ?? ["channel-a"],
        AllowedUserIds = allowedUserIds ?? [],
        ChannelAudiences = channelAudiences ?? new Dictionary<string, string>(StringComparer.Ordinal),
        ChannelAudienceOverrides = channelAudienceOverrides ?? []
    };

    private static TeamsInboundActivity CreateActivity(
        string teamId = "team-a",
        string channelId = "channel-a",
        bool isMentioned = true,
        TeamsIngressActivityKind kind = TeamsIngressActivityKind.Message) => new(
        new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "user-a",
            "tenant-a",
            "conversation-a;messageid=root-a",
            TeamsConversationScope.Channel,
            "activity-a",
            DateTimeOffset.UnixEpoch),
        "prompt",
        new TeamsReplyMetadata(null, "root-a"),
        isMentioned,
        kind: kind,
        teamId: teamId,
        channelId: channelId);
}
