// -----------------------------------------------------------------------
// <copyright file="TelegramProactiveOutboundClientContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Telegram proves the proactive outbound contract with numeric target ids.
/// The transport is sealed, so the fixture starts a real
/// <see cref="TelegramTransport"/> over the fake bot API client; the success
/// path needs an active transport before it can post.
/// </summary>
public sealed class TelegramProactiveOutboundClientContractTests(ITestOutputHelper output)
    : ProactiveOutboundClientContractTests(output)
{
    protected override string ChannelDisplayName => "Telegram";

    // Telegram target ids are numbers: positive ids address users, negative
    // ids address group chats. The canonical assertions embed these values.
    protected override string AllowedUserId => "555000111";

    protected override string DisallowedUserId => "999999999";

    protected override string AllowedChannelId => "-100200300";

    protected override string DisallowedChannelId => "-100999888";

    // Proactive Telegram sessions always key on the "chat" fallback thread.
    protected override string ExpectedThreadFor(string channelId) =>
        $"{channelId}/chat";

    protected override IChannelOutboundClient CreateClient(
        bool allowDirectMessages = true,
        bool gatewayConnected = true,
        bool gatewayAcks = true)
    {
        var options = new TelegramChannelOptions
        {
            Enabled = true,
            BotToken = new SensitiveString("12345:test-token"),
            AllowDirectMessages = allowDirectMessages,
            AllowedUserIds = [AllowedUserId],
            AllowedChatIds = [AllowedChannelId]
        };

        var transport = new TelegramTransport(
            options,
            NullLogger<TelegramTransport>.Instance,
            (_, _) => new TelegramTransportTests.FakeTelegramBotApiClient());
        transport.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        Func<object, object?> respond = msg => msg switch
        {
            StartTelegramProactiveChat spt when gatewayAcks => new TelegramProactiveChatAck(spt.SessionId),
            _ => new Status.Failure(new InvalidOperationException("session pipeline init failed"))
        };
        var gateway = Sys.ActorOf(Props.Create(() => new ProactiveGatewayResponderActor(respond)));

        return new TelegramProactiveOutboundClient(
            transport,
            options,
            () => gatewayConnected ? gateway : null);
    }
}
