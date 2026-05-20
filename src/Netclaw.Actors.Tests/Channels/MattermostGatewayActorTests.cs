// -----------------------------------------------------------------------
// <copyright file="MattermostGatewayActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

// IsAllowedUser checks are covered by AclPolicyContractTests via
// MattermostAclContractTests — only the Mattermost-specific
// SessionId-parser behavior lives here.
public sealed class MattermostGatewayActorTests
{
    [Fact]
    public void TryParseSessionId_valid_session()
    {
        var sessionId = new SessionId("channelid1234567890123456/rootpost1234567890123456");

        var result = MattermostGatewayActor.TryParseMattermostSessionId(
            sessionId, out var channelId, out var rootPostId);

        Assert.True(result);
        Assert.Equal("channelid1234567890123456", channelId.Value);
        Assert.Equal("rootpost1234567890123456", rootPostId.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-slash-here")]
    [InlineData("/missing-channel")]
    [InlineData("missing-root/")]
    public void TryParseSessionId_rejects_invalid_formats(string raw)
    {
        var sessionId = new SessionId(raw);

        var result = MattermostGatewayActor.TryParseMattermostSessionId(
            sessionId, out _, out _);

        Assert.False(result);
    }
}
