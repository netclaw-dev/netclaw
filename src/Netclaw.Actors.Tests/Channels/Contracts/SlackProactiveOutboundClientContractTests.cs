// -----------------------------------------------------------------------
// <copyright file="SlackProactiveOutboundClientContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class SlackProactiveOutboundClientContractTests(ITestOutputHelper output)
    : ProactiveOutboundClientContractTests(output)
{
    protected override string ChannelDisplayName => "Slack";

    protected override string ExpectedThreadFor(string channelId) =>
        $"{channelId}/1234567890.000001";

    protected override IChannelOutboundClient CreateClient(
        bool allowDirectMessages = true,
        bool gatewayConnected = true,
        bool gatewayAcks = true)
    {
        var options = new SlackChannelOptions
        {
            AllowDirectMessages = allowDirectMessages,
            AllowedUserIds = [AllowedUserId],
            AllowedChannelIds = [AllowedChannelId]
        };

        Func<object, object?> respond = msg => msg switch
        {
            StartProactiveThread spt when gatewayAcks => new ProactiveThreadAck(spt.SessionId),
            _ => new Status.Failure(new InvalidOperationException("session pipeline init failed"))
        };
        var gateway = Sys.ActorOf(Props.Create(() => new ProactiveGatewayResponderActor(respond)));

        return new SlackProactiveOutboundClient(
            new FakeSlackOutboundClient(),
            options,
            () => null,
            () => gatewayConnected ? gateway : null);
    }
}
