// -----------------------------------------------------------------------
// <copyright file="ProactiveOutboundClientContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Channels;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Behavioral contract for the per-channel proactive outbound clients
/// (<c>SlackProactiveOutboundClient</c>, <c>DiscordProactiveOutboundClient</c>,
/// <c>MattermostProactiveOutboundClient</c>). The clients are plain async
/// classes, but the success path asks the gateway (Ask needs a real actor
/// system to mint the promise ref), so fixtures run on TestKit and play the
/// gateway with <c>ProactiveGatewayResponderActor</c>. The returned strings are
/// LLM-visible tool results, so the expected strings are spelled out literally
/// here (not via <c>ProactiveSendFormatting</c>) to pin the canonical wording
/// independently of the production helper. Channel-unique outcomes (Discord
/// thread-name/partial-success, Slack default-channel bypass, DM open
/// failures) stay in the per-channel test files.
/// </summary>
public abstract class ProactiveOutboundClientContractTests : TestKit
{
    protected ProactiveOutboundClientContractTests(ITestOutputHelper output) : base(output: output) { }

    protected const string AllowedUserId = "user-allowed";
    protected const string DisallowedUserId = "user-bad";
    protected const string AllowedChannelId = "chan-allowed";
    protected const string DisallowedChannelId = "chan-bad";

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    /// <summary>Channel name exactly as it appears in the canonical strings ("Slack", "Discord", "Mattermost").</summary>
    protected abstract string ChannelDisplayName { get; }

    /// <summary>
    /// The <c>{channelId}/{threadKey}</c> thread reference the fixture's fake
    /// outbound client produces for a destination post to <paramref name="channelId"/>.
    /// </summary>
    protected abstract string ExpectedThreadFor(string channelId);

    /// <summary>
    /// Creates the channel's proactive client with allowlists
    /// <see cref="AllowedUserId"/>/<see cref="AllowedChannelId"/>. When
    /// <paramref name="gatewayConnected"/> is false the gateway accessor returns
    /// null; when <paramref name="gatewayAcks"/> is false the gateway replies
    /// <c>Status.Failure</c> to the proactive-thread ask (nack).
    /// </summary>
    protected abstract IChannelOutboundClient CreateClient(
        bool allowDirectMessages = true,
        bool gatewayConnected = true,
        bool gatewayAcks = true);

    [Fact]
    public async Task DmDisabled_ReturnsCanonicalError()
    {
        var client = CreateClient(allowDirectMessages: false);

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.DirectMessage, AllowedUserId, "hello"),
            CancellationToken.None);

        Assert.Equal(
            $"Error: Direct messages are disabled. Enable AllowDirectMessages in {ChannelDisplayName} configuration to send DMs.",
            result);
    }

    [Fact]
    public async Task DisallowedUser_ReturnsCanonicalError()
    {
        var client = CreateClient();

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.DirectMessage, DisallowedUserId, "hello"),
            CancellationToken.None);

        Assert.Equal($"Error: User {DisallowedUserId} is not in the allowed users list.", result);
    }

    [Fact]
    public async Task DisallowedChannel_ReturnsCanonicalError()
    {
        var client = CreateClient();

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.Destination, DisallowedChannelId, "hello"),
            CancellationToken.None);

        Assert.Equal($"Error: Channel {DisallowedChannelId} is not in the allowed channels list.", result);
    }

    [Fact]
    public async Task UnsupportedAddressKind_ReturnsCanonicalError()
    {
        var client = CreateClient();

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.User, AllowedUserId, "hello"),
            CancellationToken.None);

        Assert.Equal(
            $"Error: {ChannelDisplayName} outbound send does not support address kind 'User'.",
            result);
    }

    [Fact]
    public async Task GatewayUnavailable_ReturnsCanonicalError()
    {
        var client = CreateClient(gatewayConnected: false);

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.Destination, AllowedChannelId, "hello"),
            CancellationToken.None);

        Assert.Equal($"Error: {ChannelDisplayName} gateway is not connected.", result);
    }

    [Fact]
    public async Task SuccessfulDestinationSend_ReturnsThreadReference()
    {
        var client = CreateClient();

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.Destination, AllowedChannelId, "hello"),
            CancellationToken.None);

        Assert.Equal(
            $"Message sent to channel {AllowedChannelId}. Thread: {ExpectedThreadFor(AllowedChannelId)}",
            result);
    }

    [Fact]
    public async Task GatewayNack_ReturnsPipelineFailedFallback()
    {
        var client = CreateClient(gatewayAcks: false);

        var result = await client.SendMessageAsync(
            new ChannelSendRequest(ChannelAddressKind.Destination, AllowedChannelId, "hello"),
            CancellationToken.None);

        Assert.Equal(
            $"Message sent to channel {AllowedChannelId} but session pipeline failed to initialize. " +
            $"Thread: {ExpectedThreadFor(AllowedChannelId)}",
            result);
    }
}
