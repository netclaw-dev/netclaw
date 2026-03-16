using Netclaw.Actors.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackAclPolicyTests
{
    [Fact]
    public void EvaluateInbound_returns_team_audience_for_allowlisted_dm_user()
    {
        var message = new SlackInboundMessage(
            SlackInboundKind.Message,
            new SlackEventId("evt-1"),
            new SlackChannelId("D123"),
            null,
            new SlackEventTs("1708531200.000100"),
            new SlackUserId("U123"),
            null,
            "hello",
            null,
            false,
            true);

        var options = new SlackChannelOptions
        {
            AllowDirectMessages = true,
            AllowedUserIds = ["U123"]
        };

        var result = SlackAclPolicy.EvaluateInbound(message, options, null);

        Assert.True(result.IsAllowed);
        Assert.Equal(TrustAudience.Team, result.Audience);
        Assert.Equal(PrincipalClassification.TrustedInternal, result.Principal);
        Assert.Equal(TransportAuthenticity.Verified, result.Provenance.TransportAuthenticity);
    }

    [Fact]
    public void EvaluateInbound_denies_user_outside_allow_list()
    {
        var message = new SlackInboundMessage(
            SlackInboundKind.Message,
            new SlackEventId("evt-1"),
            new SlackChannelId("C123"),
            null,
            new SlackEventTs("1708531200.000100"),
            new SlackUserId("U999"),
            null,
            "hello",
            null,
            false,
            false);

        var options = new SlackChannelOptions
        {
            AllowedChannelIds = ["C123"],
            AllowedUserIds = ["U123"]
        };

        var result = SlackAclPolicy.EvaluateInbound(message, options, null);

        Assert.False(result.IsAllowed);
        Assert.Equal("user_not_allowed", result.DenyReason);
        Assert.Equal(TrustAudience.Public, result.Audience);
    }
}
