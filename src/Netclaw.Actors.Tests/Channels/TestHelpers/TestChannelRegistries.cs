// -----------------------------------------------------------------------
// <copyright file="TestChannelRegistries.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Slack;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal static class TestChannelRegistries
{
    public static IChannelRegistry DiscordWithProcessingRenderer(IDiscordReplyClient replyClient)
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Discord);
        var descriptor = new ChannelDescriptor(
            key,
            ChannelType.Discord,
            ChannelKind.RemoteChat,
            "Discord",
            IsEnabled: true,
            ChannelCapabilities.ReceiveMessages
                | ChannelCapabilities.SendMessages
                | ChannelCapabilities.ThreadedConversations
                | ChannelCapabilities.InteractiveApproval,
            ToolIntents: new HashSet<ChannelToolIntentKind>
            {
                ChannelToolIntentKind.SendMessage
            },
            AddressKinds: new HashSet<ChannelAddressKind>
            {
                ChannelAddressKind.Destination,
                ChannelAddressKind.Thread
            },
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>
            {
                ChannelOutputEffectKind.TextMessage,
                ChannelOutputEffectKind.InteractiveApproval,
                ChannelOutputEffectKind.ProcessingIndicator
            });

        return new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            [],
            outputRenderers: [new DiscordProcessingOutputRenderer(replyClient)]);
    }

    public static IChannelRegistry SlackWithProcessingRenderer(ISlackReplyClient replyClient)
        => SlackWithProcessingRenderer(new SlackProcessingOutputRenderer(replyClient));

    public static IChannelRegistry SlackWithProcessingRenderer(IChannelOutputRenderer renderer)
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var descriptor = new ChannelDescriptor(
            key,
            ChannelType.Slack,
            ChannelKind.RemoteChat,
            "Slack",
            IsEnabled: true,
            ChannelCapabilities.ReceiveMessages
                | ChannelCapabilities.SendMessages
                | ChannelCapabilities.ThreadedConversations
                | ChannelCapabilities.InteractiveApproval,
            ToolIntents: new HashSet<ChannelToolIntentKind>
            {
                ChannelToolIntentKind.SendMessage
            },
            AddressKinds: new HashSet<ChannelAddressKind>
            {
                ChannelAddressKind.Destination,
                ChannelAddressKind.Thread
            },
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>
            {
                ChannelOutputEffectKind.TextMessage,
                ChannelOutputEffectKind.InteractiveApproval,
                ChannelOutputEffectKind.ProcessingIndicator
            });

        return new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            [],
            outputRenderers: [renderer]);
    }
}
