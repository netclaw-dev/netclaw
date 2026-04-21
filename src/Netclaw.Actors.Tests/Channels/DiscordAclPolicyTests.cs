using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordAclPolicyTests
{
    [Fact]
    public void EvaluateInbound_denies_dm_when_direct_messages_disabled()
    {
        var message = CreateMessage(isDirectMessage: true, channelId: "dm-1", senderId: "u-1");
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            AllowDirectMessages = false,
            AllowedUserIds = ["u-1"]
        };

        var decision = DiscordAclPolicy.EvaluateInbound(message, options, defaultChannelId: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal("direct_messages_disabled", decision.DenyReason);
    }

    [Fact]
    public void EvaluateInbound_denies_non_dm_channel_not_in_allow_list()
    {
        var message = CreateMessage(isDirectMessage: false, channelId: "ch-denied", senderId: "u-1");
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            AllowedChannelIds = ["ch-allowed"],
            AllowedUserIds = ["u-1"]
        };

        var decision = DiscordAclPolicy.EvaluateInbound(message, options, defaultChannelId: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal("channel_not_allowed", decision.DenyReason);
    }

    [Fact]
    public void EvaluateInbound_allows_explicit_user_and_channel()
    {
        var message = CreateMessage(isDirectMessage: false, channelId: "ch-allowed", senderId: "u-1");
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            AllowedChannelIds = ["ch-allowed"],
            AllowedUserIds = ["u-1"]
        };

        var decision = DiscordAclPolicy.EvaluateInbound(message, options, defaultChannelId: null);

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.DenyReason);
        Assert.Equal(TrustAudience.Team, decision.Audience);
        Assert.Equal(PrincipalClassification.TrustedInternal, decision.Principal);
    }

    [Fact]
    public void EvaluateInbound_denies_missing_sender_id()
    {
        var message = CreateMessage(isDirectMessage: false, channelId: "ch-1", senderId: "");
        var options = new DiscordChannelOptions { Enabled = true, AllowedChannelIds = ["ch-1"] };

        var decision = DiscordAclPolicy.EvaluateInbound(message, options, defaultChannelId: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal("missing_user_id", decision.DenyReason);
    }

    [Fact]
    public void EvaluateInbound_allows_default_channel_id()
    {
        var message = CreateMessage(isDirectMessage: false, channelId: "ch-default", senderId: "u-1");
        var options = new DiscordChannelOptions { Enabled = true, AllowedChannelIds = [] };

        var decision = DiscordAclPolicy.EvaluateInbound(
            message, options, defaultChannelId: new DiscordChannelId("ch-default"));

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void EvaluateInbound_allows_all_users_when_allowlist_empty()
    {
        var message = CreateMessage(isDirectMessage: false, channelId: "ch-1", senderId: "u-anybody");
        var options = new DiscordChannelOptions { Enabled = true, AllowedChannelIds = ["ch-1"], AllowedUserIds = [] };

        var decision = DiscordAclPolicy.EvaluateInbound(message, options, defaultChannelId: null);

        Assert.True(decision.IsAllowed);
        Assert.Equal(PrincipalClassification.UntrustedExternal, decision.Principal);
    }

    [Fact]
    public void EvaluateInbound_allows_dm_when_enabled()
    {
        var message = CreateMessage(isDirectMessage: true, channelId: "dm-1", senderId: "u-1");
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            AllowDirectMessages = true,
            AllowedUserIds = ["u-1"]
        };

        var decision = DiscordAclPolicy.EvaluateInbound(message, options, defaultChannelId: null);

        Assert.True(decision.IsAllowed);
        Assert.Equal(TrustAudience.Team, decision.Audience);
    }

    [Fact]
    public void EvaluateInbound_resolves_channel_audience_override()
    {
        var message = CreateMessage(isDirectMessage: false, channelId: "ch-public", senderId: "u-1");
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            AllowedChannelIds = ["ch-public"],
            AllowedUserIds = ["u-1"],
            ChannelAudiences = new Dictionary<string, string> { ["ch-public"] = "public" }
        };

        var decision = DiscordAclPolicy.EvaluateInbound(message, options, defaultChannelId: null);

        Assert.True(decision.IsAllowed);
        Assert.Equal(TrustAudience.Public, decision.Audience);
    }

    [Fact]
    public void EvaluateInbound_resolves_dm_audience_override()
    {
        var message = CreateMessage(isDirectMessage: true, channelId: "dm-1", senderId: "u-1");
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            AllowDirectMessages = true,
            AllowedUserIds = ["u-1"],
            ChannelAudiences = new Dictionary<string, string> { ["dm"] = "personal" }
        };

        var decision = DiscordAclPolicy.EvaluateInbound(message, options, defaultChannelId: null);

        Assert.True(decision.IsAllowed);
        Assert.Equal(TrustAudience.Personal, decision.Audience);
    }

    [Fact]
    public void EvaluateInbound_denies_invalid_channel_audience()
    {
        var message = CreateMessage(isDirectMessage: false, channelId: "ch-1", senderId: "u-1");
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            AllowedChannelIds = ["ch-1"],
            AllowedUserIds = ["u-1"],
            ChannelAudiences = new Dictionary<string, string> { ["ch-1"] = "invalid_audience" }
        };

        var decision = DiscordAclPolicy.EvaluateInbound(message, options, defaultChannelId: null);

        Assert.False(decision.IsAllowed);
        Assert.StartsWith("invalid_channel_audience:", decision.DenyReason);
    }

    [Fact]
    public void EvaluateInbound_falls_back_to_public_for_non_explicit_user_and_channel()
    {
        var message = CreateMessage(isDirectMessage: false, channelId: "ch-default", senderId: "u-random");
        var options = new DiscordChannelOptions { Enabled = true, AllowedChannelIds = [] };

        var decision = DiscordAclPolicy.EvaluateInbound(
            message, options, defaultChannelId: new DiscordChannelId("ch-default"));

        Assert.True(decision.IsAllowed);
        Assert.Equal(TrustAudience.Public, decision.Audience);
        Assert.Equal(PrincipalClassification.UntrustedExternal, decision.Principal);
    }

    private static DiscordGatewayMessage CreateMessage(bool isDirectMessage, string channelId, string senderId)
    {
        return new DiscordGatewayMessage(
            EventId: new DiscordEventId(Guid.NewGuid().ToString("N")),
            ChannelId: new DiscordChannelId(channelId),
            ReplyChannelId: new DiscordReplyChannelId(channelId),
            MessageId: new DiscordMessageId("m-1"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("m-1"),
            RootMessageId: new DiscordMessageId("m-1"),
            SenderId: new DiscordUserId(senderId),
            IsBotMessage: false,
            IsDirectMessage: isDirectMessage,
            Text: "hello",
            ReceivedAt: TimeProvider.System.GetUtcNow());
    }
}
