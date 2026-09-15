// -----------------------------------------------------------------------
// <copyright file="TelegramProactiveOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Telegram;

public sealed class TelegramProactiveOutboundClient(
    TelegramTransport transport,
    TelegramChannelOptions options,
    Func<IActorRef?> gatewayAccessor) : IChannelOutboundClient
{
    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Telegram);

    public async Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gateway = gatewayAccessor();
        if (gateway is null)
            return ProactiveSendFormatting.GatewayNotConnected("Telegram");

        if (!long.TryParse(request.TargetId, out var targetId) || targetId == 0)
            return "Error: Telegram destination ID must be a non-zero integer.";

        var isDirectMessage = request.AddressKind == ChannelAddressKind.DirectMessage;
        if (isDirectMessage)
        {
            if (!options.AllowDirectMessages)
                return ProactiveSendFormatting.DirectMessagesDisabled("Telegram");
            if (!options.AllowedUserIds.Contains(request.TargetId, StringComparer.Ordinal))
                return ProactiveSendFormatting.UserNotAllowed(request.TargetId);
        }
        else if (request.AddressKind == ChannelAddressKind.Destination)
        {
            if (!TelegramAclPolicy.IsAllowedChat(targetId, options))
                return ProactiveSendFormatting.ChannelNotAllowed(request.TargetId);
        }
        else
        {
            return ProactiveSendFormatting.UnsupportedAddressKind("Telegram", request.AddressKind);
        }

        try
        {
            await transport.SendTextAsync(targetId, request.Text, ct);
        }
        catch (Exception ex)
        {
            return ProactiveSendFormatting.PostFailed("Telegram", ex.Message);
        }

        var sessionId = SessionIdFormat.Build(request.TargetId, "chat");
        var target = ProactiveSendFormatting.DescribeTarget(isDirectMessage, request.TargetId);
        try
        {
            await gateway.Ask<TelegramProactiveChatAck>(
                new StartTelegramProactiveChat(new TelegramChatId(targetId), sessionId, isDirectMessage),
                ProactiveSendFormatting.ProactiveThreadAckTimeout,
                ct);
        }
        catch (Exception)
        {
            return ProactiveSendFormatting.SentButPipelineFailed(target, sessionId.Value);
        }

        return ProactiveSendFormatting.Sent(target, sessionId.Value);
    }
}
