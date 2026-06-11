// -----------------------------------------------------------------------
// <copyright file="MattermostConnectFailureClassifierTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public class MattermostConnectFailureClassifierTests
{
    [Theory]
    [InlineData("401")]
    [InlineData("unauthorized")]
    [InlineData("invalid_token")]
    [InlineData("session_expired")]
    [InlineData("invalid or expired")]
    [InlineData("invalid session")]
    [InlineData("403")]
    [InlineData("no such host")]
    [InlineData("name or service not known")]
    public void FatalSignals_ClassifyAsFatal(string signal)
    {
        var result = MattermostConnectFailureClassifier.Classify(
            new Exception($"Connection failed: {signal}"));

        Assert.Equal(ChannelConnectFailureKind.Fatal, result.Kind);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void FatalSignal_WrappedInOuterException_IsStillFatal()
    {
        var wrapped = new Exception(
            "WebSocket connection was closed",
            new Exception("server returned 401 unauthorized"));

        var result = MattermostConnectFailureClassifier.Classify(wrapped);

        Assert.Equal(ChannelConnectFailureKind.Fatal, result.Kind);
    }

    [Fact]
    public void GenericNetworkError_ClassifiesAsTransient()
    {
        var result = MattermostConnectFailureClassifier.Classify(
            new TimeoutException("gateway did not respond"));

        Assert.Equal(ChannelConnectFailureKind.Transient, result.Kind);
    }

    [Fact]
    public void AlreadyClassified_IsReturnedUnchanged()
    {
        var original = new ChannelConnectException(ChannelConnectFailureKind.Fatal, "boom");

        var result = MattermostConnectFailureClassifier.Classify(original);

        Assert.Same(original, result);
    }

    [Fact]
    public void FatalSignal_CaseInsensitive()
    {
        var result = MattermostConnectFailureClassifier.Classify(
            new Exception("UNAUTHORIZED access denied"));

        Assert.Equal(ChannelConnectFailureKind.Fatal, result.Kind);
    }
}
