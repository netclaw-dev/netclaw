// -----------------------------------------------------------------------
// <copyright file="DiscordProactiveOutboundClientContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class DiscordProactiveOutboundClientContractTests(ITestOutputHelper output)
    : ProactiveOutboundClientContractTests(output)
{
    protected override string ChannelDisplayName => "Discord";

    protected override string ExpectedThreadFor(string channelId) =>
        $"{channelId}/thread-{channelId}";

    protected override IChannelOutboundClient CreateClient(
        bool allowDirectMessages = true,
        bool gatewayConnected = true,
        bool gatewayAcks = true)
    {
        var options = new DiscordChannelOptions
        {
            Enabled = true,
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

        return new DiscordProactiveOutboundClient(
            new FakeDiscordOutboundClient(),
            options,
            () => gatewayConnected ? gateway : null);
    }
}
