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
