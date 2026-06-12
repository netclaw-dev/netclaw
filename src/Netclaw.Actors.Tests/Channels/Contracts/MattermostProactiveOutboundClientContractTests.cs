// -----------------------------------------------------------------------
// <copyright file="MattermostProactiveOutboundClientContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class MattermostProactiveOutboundClientContractTests(ITestOutputHelper output)
    : ProactiveOutboundClientContractTests(output)
{
    protected override string ChannelDisplayName => "Mattermost";

    protected override string ExpectedThreadFor(string channelId) =>
        $"{channelId}/root-{channelId}";

    protected override IChannelOutboundClient CreateClient(
        bool allowDirectMessages = true,
        bool gatewayConnected = true,
        bool gatewayAcks = true)
    {
        var options = new MattermostChannelOptions
        {
            AllowDirectMessages = allowDirectMessages,
            AllowedUserIds = [AllowedUserId],
            AllowedChannelIds = [AllowedChannelId]
        };

        Func<object, object?> respond = msg => msg switch
        {
            StartMattermostProactiveThread spt when gatewayAcks => new MattermostProactiveThreadAck(spt.SessionId),
            _ => new Status.Failure(new InvalidOperationException("session pipeline init failed"))
        };
        var gateway = Sys.ActorOf(Props.Create(() => new ProactiveGatewayResponderActor(respond)));

        return new MattermostProactiveOutboundClient(
            new FakeMattermostOutboundClient(),
            options,
            () => null,
            () => gatewayConnected ? gateway : null);
    }
}
