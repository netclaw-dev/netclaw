// -----------------------------------------------------------------------
// <copyright file="MattermostGatewayActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

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

    [Fact]
    public void IsAllowedUser_empty_allowlist_permits_all()
    {
        var options = new MattermostChannelOptions { AllowedUserIds = [] };
        Assert.True(MattermostAclPolicy.IsAllowedUser(new MattermostUserId("any-user"), options));
    }

    [Fact]
    public void IsAllowedUser_rejects_unlisted_user()
    {
        var options = new MattermostChannelOptions { AllowedUserIds = ["allowed-user"] };
        Assert.False(MattermostAclPolicy.IsAllowedUser(new MattermostUserId("other-user"), options));
    }

    [Fact]
    public void IsAllowedUser_permits_listed_user()
    {
        var options = new MattermostChannelOptions { AllowedUserIds = ["allowed-user"] };
        Assert.True(MattermostAclPolicy.IsAllowedUser(new MattermostUserId("allowed-user"), options));
    }
}
