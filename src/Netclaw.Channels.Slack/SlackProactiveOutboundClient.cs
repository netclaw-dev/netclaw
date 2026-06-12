// -----------------------------------------------------------------------
// <copyright file="SlackProactiveOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Slack implementation of <see cref="IChannelOutboundClient"/>: ACL-checks the
/// destination, posts a proactive message to a channel (or opens a DM channel
/// first), and wires the new thread into the actor hierarchy so user replies
/// route back to a live session. Distinct from <see cref="ISlackOutboundClient"/>,
/// which is the raw Slack API transport this class orchestrates.
/// </summary>
public sealed class SlackProactiveOutboundClient : IChannelOutboundClient
{
    private readonly ISlackOutboundClient _outboundClient;
    private readonly SlackChannelOptions _options;
    private readonly Func<SlackChannelId?> _defaultChannelIdAccessor;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public SlackProactiveOutboundClient(
        ISlackOutboundClient outboundClient,
        SlackChannelOptions options,
        Func<SlackChannelId?> defaultChannelIdAccessor,
        Func<IActorRef?> gatewayAccessor)
    {
        _outboundClient = outboundClient;
        _options = options;
        _defaultChannelIdAccessor = defaultChannelIdAccessor;
        _gatewayAccessor = gatewayAccessor;
    }

    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Slack);

    public async Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gateway = _gatewayAccessor();
        if (gateway is null)
            return ProactiveSendFormatting.GatewayNotConnected("Slack");

        var isDirectMessage = request.AddressKind == ChannelAddressKind.DirectMessage;

        SlackChannelId targetChannelId;
        if (isDirectMessage)
        {
            if (!_options.AllowDirectMessages)
                return ProactiveSendFormatting.DirectMessagesDisabled("Slack");

            var userId = new SlackUserId(request.TargetId);

            if (!SlackAclPolicy.IsAllowedUser(userId, _options))
                return ProactiveSendFormatting.UserNotAllowed(userId.Value);

            try
            {
                targetChannelId = await _outboundClient.OpenDmChannelAsync(userId, ct);
            }
            catch (Exception ex)
            {
                return ProactiveSendFormatting.OpenDmChannelFailed(ex.Message);
            }
        }
        else if (request.AddressKind == ChannelAddressKind.Destination)
        {
            targetChannelId = new SlackChannelId(request.TargetId);

            if (!SlackAclPolicy.IsAllowedChannel(targetChannelId, _options, _defaultChannelIdAccessor()))
                return ProactiveSendFormatting.ChannelNotAllowed(targetChannelId.Value);
        }
        else
        {
            return ProactiveSendFormatting.UnsupportedAddressKind("Slack", request.AddressKind);
        }

        SlackNewThread result;
        try
        {
            result = await _outboundClient.PostNewThreadAsync(targetChannelId, request.Text, ct);
        }
        catch (Exception ex)
        {
            return ProactiveSendFormatting.PostFailed("Slack", ex.Message);
        }

        var sessionId = SessionIdFormat.Build(result.ChannelId.Value, result.ThreadTs.Value);
        var target = ProactiveSendFormatting.DescribeTarget(isDirectMessage, request.TargetId);

        try
        {
            await gateway.Ask<ProactiveThreadAck>(
                new StartProactiveThread(result.ChannelId, result.ThreadTs, sessionId),
                ProactiveSendFormatting.ProactiveThreadAckTimeout,
                ct);
        }
        catch (Exception)
        {
            // Message was already posted to Slack; the pipeline just didn't initialize
            return ProactiveSendFormatting.SentButPipelineFailed(target, sessionId.Value);
        }

        return ProactiveSendFormatting.Sent(target, sessionId.Value);
    }
}
