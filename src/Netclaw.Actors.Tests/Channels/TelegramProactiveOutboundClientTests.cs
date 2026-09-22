// -----------------------------------------------------------------------
// <copyright file="TelegramProactiveOutboundClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Telegram-unique proactive outcomes that have no neutral contract mapping:
/// the numeric-target format guard, the DM success wording, and post failure.
/// The shared canonical errors live in
/// <see cref="Contracts.TelegramProactiveOutboundClientContractTests"/>.
/// </summary>
public sealed class TelegramProactiveOutboundClientTests(ITestOutputHelper output) : TestKit(output: output)
{
    private const string AllowedUserId = "555000111";
    private const string AllowedGroupId = "-100200300";

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Non_numeric_target_is_rejected_with_format_error()
    {
        var client = CreateClient(Options(), () => NonNullGateway());

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.Destination, "chan-allowed", "hello"),
            TestContext.Current.CancellationToken);

        // The format guard runs before ACL evaluation, so a non-numeric target
        // reports the alphabet error even when the chat is also unlisted.
        Assert.Equal("Error: Telegram destination ID must be a non-zero integer.", result);
    }

    [Fact]
    public async Task Zero_target_is_rejected_with_format_error()
    {
        var client = CreateClient(Options(), () => NonNullGateway());

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.Destination, "0", "hello"),
            TestContext.Current.CancellationToken);

        Assert.Equal("Error: Telegram destination ID must be a non-zero integer.", result);
    }

    [Fact]
    public async Task Successful_direct_message_send_reports_user_target_and_thread()
    {
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        var client = CreateClient(Options(), () => AckingGateway(), fake);

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.DirectMessage, AllowedUserId, "hello"),
            TestContext.Current.CancellationToken);

        Assert.Equal($"Message sent to user {AllowedUserId}. Thread: {AllowedUserId}/chat", result);

        // The post must reach the transport before the session wiring ask.
        var post = Assert.Single(fake.SentTexts);
        Assert.Equal(555000111, post.ChatId);
        Assert.Equal("hello", post.Text);
    }

    [Fact]
    public async Task Post_failure_reports_post_failed_with_transport_reason()
    {
        var fake = new TelegramTransportTests.FakeTelegramBotApiClient();
        fake.SendMessageFailures.Enqueue(new InvalidOperationException("telegram api down"));
        var client = CreateClient(Options(), () => NonNullGateway(), fake);

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.Destination, AllowedGroupId, "hello"),
            TestContext.Current.CancellationToken);

        Assert.Equal("Error: Failed to post message to Telegram: telegram api down", result);
    }

    private static TelegramChannelOptions Options() => new()
    {
        Enabled = true,
        BotToken = new SensitiveString("12345:test-token"),
        AllowDirectMessages = true,
        AllowedUserIds = [AllowedUserId],
        AllowedChatIds = [AllowedGroupId]
    };

    private static FakeProactiveGateway NonNullGateway() => new(_ => null);

    private IActorRef AckingGateway() =>
        Sys.ActorOf(Props.Create(() => new ProactiveGatewayResponderActor(
            _ => new TelegramProactiveChatAck(new SessionId("proactive-probe")))));

    private static TelegramProactiveOutboundClient CreateClient(
        TelegramChannelOptions options,
        Func<IActorRef?> gateway,
        TelegramTransportTests.FakeTelegramBotApiClient? fake = null)
    {
        var transport = new TelegramTransport(
            options,
            NullLogger<TelegramTransport>.Instance,
            (_, _) => fake ?? new TelegramTransportTests.FakeTelegramBotApiClient());
        transport.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        return new TelegramProactiveOutboundClient(transport, options, gateway);
    }
}
