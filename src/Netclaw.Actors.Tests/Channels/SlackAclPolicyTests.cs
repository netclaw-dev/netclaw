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

    [Fact]
    public void EvaluateInbound_dm_with_channel_audiences_dm_personal_returns_personal()
    {
        var message = CreateDm("U123");
        var options = new SlackChannelOptions
        {
            AllowDirectMessages = true,
            ChannelAudiences = new Dictionary<string, string> { ["dm"] = "personal" }
        };

        var result = SlackAclPolicy.EvaluateInbound(message, options, null);

        Assert.True(result.IsAllowed);
        Assert.Equal(TrustAudience.Personal, result.Audience);
    }

    [Fact]
    public void EvaluateInbound_explicit_channel_id_override_takes_precedence()
    {
        var message = CreateChannelMessage("C456", "U123");
        var options = new SlackChannelOptions
        {
            AllowedChannelIds = ["C456"],
            ChannelAudiences = new Dictionary<string, string> { ["C456"] = "personal" }
        };

        var result = SlackAclPolicy.EvaluateInbound(message, options, null);

        Assert.True(result.IsAllowed);
        Assert.Equal(TrustAudience.Personal, result.Audience);
    }

    [Fact]
    public void EvaluateInbound_missing_channel_audience_falls_back_to_heuristic()
    {
        var message = CreateChannelMessage("C789", "U123");
        var options = new SlackChannelOptions
        {
            AllowedChannelIds = ["C789"],
            ChannelAudiences = new Dictionary<string, string> { ["dm"] = "personal" }
        };

        var result = SlackAclPolicy.EvaluateInbound(message, options, null);

        Assert.True(result.IsAllowed);
        // Explicit channel → Team via heuristic fallback
        Assert.Equal(TrustAudience.Team, result.Audience);
    }

    [Fact]
    public void EvaluateInbound_dm_without_channel_audiences_returns_team()
    {
        var message = CreateDm("U123");
        var options = new SlackChannelOptions
        {
            AllowDirectMessages = true
        };

        var result = SlackAclPolicy.EvaluateInbound(message, options, null);

        Assert.True(result.IsAllowed);
        Assert.Equal(TrustAudience.Team, result.Audience);
    }

    [Fact]
    public void EvaluateInbound_dm_channel_id_in_audiences_takes_precedence_over_dm_key()
    {
        // DM channel ID "D123" is explicitly mapped to "public",
        // while "dm" key says "personal" — explicit ID wins.
        var message = CreateDm("U123");
        var options = new SlackChannelOptions
        {
            AllowDirectMessages = true,
            ChannelAudiences = new Dictionary<string, string>
            {
                ["D123"] = "public",
                ["dm"] = "personal"
            }
        };

        var result = SlackAclPolicy.EvaluateInbound(message, options, null);

        Assert.True(result.IsAllowed);
        Assert.Equal(TrustAudience.Public, result.Audience);
    }

    [Fact]
    public void EvaluateInbound_invalid_channel_audience_value_denies_message()
    {
        var message = CreateDm("U123");
        var options = new SlackChannelOptions
        {
            AllowDirectMessages = true,
            ChannelAudiences = new Dictionary<string, string> { ["dm"] = "persoanl" } // typo
        };

        var result = SlackAclPolicy.EvaluateInbound(message, options, null);

        Assert.False(result.IsAllowed);
        Assert.Contains("invalid_channel_audience", result.DenyReason);
        Assert.Contains("persoanl", result.DenyReason);
    }

    [Fact]
    public void EvaluateInbound_invalid_channel_id_audience_value_denies_message()
    {
        var message = CreateChannelMessage("C456", "U123");
        var options = new SlackChannelOptions
        {
            AllowedChannelIds = ["C456"],
            ChannelAudiences = new Dictionary<string, string> { ["C456"] = "pubilc" } // typo
        };

        var result = SlackAclPolicy.EvaluateInbound(message, options, null);

        Assert.False(result.IsAllowed);
        Assert.Contains("invalid_channel_audience", result.DenyReason);
        Assert.Contains("pubilc", result.DenyReason);
    }

    private static SlackInboundMessage CreateDm(string userId) => new(
        SlackInboundKind.Message,
        new SlackEventId("evt-1"),
        new SlackChannelId("D123"),
        null,
        new SlackEventTs("1708531200.000100"),
        new SlackUserId(userId),
        null,
        "hello",
        null,
        false,
        true);

    private static SlackInboundMessage CreateChannelMessage(string channelId, string userId) => new(
        SlackInboundKind.Message,
        new SlackEventId("evt-1"),
        new SlackChannelId(channelId),
        null,
        new SlackEventTs("1708531200.000100"),
        new SlackUserId(userId),
        null,
        "hello",
        null,
        false,
        false);
}
