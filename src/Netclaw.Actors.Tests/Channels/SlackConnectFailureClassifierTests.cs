// -----------------------------------------------------------------------
// <copyright file="SlackConnectFailureClassifierTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public class SlackConnectFailureClassifierTests
{
    [Theory]
    [InlineData("invalid_auth")]
    [InlineData("account_inactive")]
    [InlineData("token_revoked")]
    [InlineData("token_expired")]
    [InlineData("not_authed")]
    [InlineData("missing_scope")]
    [InlineData("invalid_app_id")]
    public void FatalErrorCodes_ClassifyAsFatal(string errorCode)
    {
        var result = SlackConnectFailureClassifier.Classify(new Exception(errorCode));

        Assert.Equal(ChannelConnectFailureKind.Fatal, result.Kind);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void FatalErrorCode_WrappedInInnerException_IsStillFatal()
    {
        var wrapped = new Exception(
            "Slack socket mode connect failed",
            new Exception("invalid_auth"));

        var result = SlackConnectFailureClassifier.Classify(wrapped);

        Assert.Equal(ChannelConnectFailureKind.Fatal, result.Kind);
    }

    [Fact]
    public void GenericNetworkError_ClassifiesAsTransient()
    {
        var result = SlackConnectFailureClassifier.Classify(
            new TimeoutException("connection refused"));

        Assert.Equal(ChannelConnectFailureKind.Transient, result.Kind);
    }

    [Fact]
    public void AlreadyClassified_IsReturnedUnchanged()
    {
        var original = new ChannelConnectException(ChannelConnectFailureKind.Transient, "boom");

        var result = SlackConnectFailureClassifier.Classify(original);

        Assert.Same(original, result);
    }
}
