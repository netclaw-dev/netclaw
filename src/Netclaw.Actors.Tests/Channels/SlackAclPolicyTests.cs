// -----------------------------------------------------------------------
// <copyright file="SlackAclPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Slack-specific ACL edge cases not covered by the shared
/// <see cref="Contracts.SlackAclContractTests"/> contract suite.
/// </summary>
public sealed class SlackAclPolicyTests
{
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
        Assert.Equal(TrustAudience.Team, result.Audience);
    }

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
