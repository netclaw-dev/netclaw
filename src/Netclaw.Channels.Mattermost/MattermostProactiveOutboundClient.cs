// -----------------------------------------------------------------------
// <copyright file="MattermostProactiveOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Mattermost;

/// <summary>
/// Mattermost implementation of <see cref="IChannelOutboundClient"/>: ACL-checks
/// the destination, posts a proactive message to a channel (or opens a DM
/// channel first), and wires the new thread into the actor hierarchy so user
/// replies route back to a live session. Distinct from
/// <see cref="IMattermostOutboundClient"/>, which is the raw Mattermost API
/// transport this class orchestrates.
/// </summary>
public sealed class MattermostProactiveOutboundClient : IChannelOutboundClient
{
    private readonly IMattermostOutboundClient _outboundClient;
    private readonly MattermostChannelOptions _options;
    private readonly Func<MattermostChannelId?> _defaultChannelIdAccessor;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public MattermostProactiveOutboundClient(
        IMattermostOutboundClient outboundClient,
        MattermostChannelOptions options,
        Func<MattermostChannelId?> defaultChannelIdAccessor,
        Func<IActorRef?> gatewayAccessor)
    {
        _outboundClient = outboundClient;
        _options = options;
        _defaultChannelIdAccessor = defaultChannelIdAccessor;
        _gatewayAccessor = gatewayAccessor;
    }

    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);

    public async Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gateway = _gatewayAccessor();
        if (gateway is null)
            return ProactiveSendFormatting.GatewayNotConnected("Mattermost");

        var isDirectMessage = request.AddressKind == ChannelAddressKind.DirectMessage;

        MattermostChannelId targetChannelId;
        MattermostUserId? directMessageUserId = null;
        if (isDirectMessage)
        {
            if (!_options.AllowDirectMessages)
                return ProactiveSendFormatting.DirectMessagesDisabled("Mattermost");

            var userId = new MattermostUserId(request.TargetId);

            if (!MattermostAclPolicy.IsAllowedUser(userId, _options))
                return ProactiveSendFormatting.UserNotAllowed(userId.Value);

            try
            {
                targetChannelId = await _outboundClient.OpenDmChannelAsync(userId, ct);
            }
            catch (Exception ex)
            {
                return ProactiveSendFormatting.OpenDmChannelFailed(ex.Message);
            }

            // DM channel ids are ephemeral, so the conversation actor must
            // re-validate against the user ACL rather than the channel ACL.
            directMessageUserId = userId;
        }
        else if (request.AddressKind == ChannelAddressKind.Destination)
        {
            targetChannelId = new MattermostChannelId(request.TargetId);

            if (!MattermostAclPolicy.IsAllowedChannel(targetChannelId, _options, _defaultChannelIdAccessor()))
                return ProactiveSendFormatting.ChannelNotAllowed(targetChannelId.Value);
        }
        else
        {
            return ProactiveSendFormatting.UnsupportedAddressKind("Mattermost", request.AddressKind);
        }

        MattermostNewThread result;
        try
        {
            result = await _outboundClient.PostNewThreadAsync(targetChannelId, request.Text, ct);
        }
        catch (Exception ex)
        {
            return ProactiveSendFormatting.PostFailed("Mattermost", ex.Message);
        }

        var sessionId = SessionIdFormat.Build(result.ChannelId.Value, result.RootPostId.Value);
        var target = ProactiveSendFormatting.DescribeTarget(isDirectMessage, request.TargetId);

        try
        {
            await gateway.Ask<MattermostProactiveThreadAck>(
                new StartMattermostProactiveThread(result.ChannelId, result.RootPostId, sessionId, directMessageUserId),
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
