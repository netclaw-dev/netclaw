// -----------------------------------------------------------------------
// <copyright file="DiscordConnectFailureClassifierTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Discord.Net;
using Netclaw.Channels;
using Netclaw.Channels.Discord.Transport;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public class DiscordConnectFailureClassifierTests
{
    [Theory]
    [InlineData(4004)] // authentication failed
    [InlineData(4010)] // invalid shard
    [InlineData(4011)] // sharding required
    [InlineData(4012)] // invalid API version
    [InlineData(4013)] // invalid intent(s)
    [InlineData(4014)] // disallowed intent(s)
    public void FatalCloseCodes_ClassifyAsFatal(int closeCode)
    {
        var result = DiscordConnectFailureClassifier.Classify(new WebSocketClosedException(closeCode));

        Assert.Equal(ChannelConnectFailureKind.Fatal, result.Kind);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void DisallowedIntents_4014_NamesTheMessageContentIntent()
    {
        var result = DiscordConnectFailureClassifier.Classify(
            new WebSocketClosedException(4014, "Disallowed intent(s)."));

        Assert.Equal(ChannelConnectFailureKind.Fatal, result.Kind);
        Assert.Contains("Message Content", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FatalCloseCode_WrappedInOuterException_IsStillFatal()
    {
        // Discord.Net surfaces 4014 as a WebSocketException wrapping a
        // WebSocketClosedException — the classifier must unwrap the chain.
        var wrapped = new Exception(
            "WebSocket connection was closed",
            new WebSocketClosedException(4014));

        var result = DiscordConnectFailureClassifier.Classify(wrapped);

        Assert.Equal(ChannelConnectFailureKind.Fatal, result.Kind);
    }

    [Theory]
    [InlineData(4000)] // unknown error
    [InlineData(4008)] // rate limited
    [InlineData(4009)] // session timed out
    public void RecoverableCloseCodes_ClassifyAsTransient(int closeCode)
    {
        var result = DiscordConnectFailureClassifier.Classify(new WebSocketClosedException(closeCode));

        Assert.Equal(ChannelConnectFailureKind.Transient, result.Kind);
    }

    [Fact]
    public void GenericNetworkError_ClassifiesAsTransient()
    {
        var result = DiscordConnectFailureClassifier.Classify(
            new TimeoutException("gateway did not respond"));

        Assert.Equal(ChannelConnectFailureKind.Transient, result.Kind);
    }

    [Fact]
    public void AlreadyClassified_IsReturnedUnchanged()
    {
        var original = new ChannelConnectException(ChannelConnectFailureKind.Fatal, "boom");

        var result = DiscordConnectFailureClassifier.Classify(original);

        Assert.Same(original, result);
    }
}
