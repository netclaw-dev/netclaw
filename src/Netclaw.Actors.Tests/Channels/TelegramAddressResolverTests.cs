// -----------------------------------------------------------------------
// <copyright file="TelegramAddressResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramAddressResolverTests
{
    private static readonly ChannelDescriptorKey TelegramKey =
        ChannelDescriptorKey.FromChannelType(ChannelType.Telegram);

    [Fact]
    public async Task Resolves_only_configured_private_user()
    {
        var resolver = new TelegramAddressResolver(new TelegramChannelOptions
        {
            AllowDirectMessages = true,
            AllowedUserIds = ["6875639362"]
        });

        var allowed = await resolver.ResolveAsync(new ChannelAddressResolutionRequest(
            TelegramKey, ChannelAddressKind.DirectMessage, "6875639362"), TestContext.Current.CancellationToken);
        var denied = await resolver.ResolveAsync(new ChannelAddressResolutionRequest(
            TelegramKey, ChannelAddressKind.DirectMessage, "123456"), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Resolved, allowed.Status);
        Assert.Equal("6875639362", Assert.Single(allowed.Candidates).StableId);
        Assert.Equal(ChannelAddressResolutionStatus.NotFound, denied.Status);
    }

    [Fact]
    public async Task Lists_only_configured_group_destinations()
    {
        var resolver = new TelegramAddressResolver(new TelegramChannelOptions
        {
            AllowedChatIds = ["-5364308250", "-5364308250", "invalid"]
        });

        var result = await resolver.ListDestinationsAsync(TestContext.Current.CancellationToken);

        var destination = Assert.Single(result.Candidates);
        Assert.Equal("-5364308250", destination.StableId);
        Assert.Equal(ChannelAddressKind.Destination, destination.AddressKind);
    }

    [Fact]
    public async Task Rejects_unconfigured_group()
    {
        var resolver = new TelegramAddressResolver(new TelegramChannelOptions
        {
            AllowedChatIds = ["-5364308250"]
        });

        var result = await resolver.ResolveAsync(new ChannelAddressResolutionRequest(
            TelegramKey, ChannelAddressKind.Destination, "-999"), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.NotFound, result.Status);
    }
}
