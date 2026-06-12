// -----------------------------------------------------------------------
// <copyright file="DiscordProactiveOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Discord;

/// <summary>
/// Discord implementation of <see cref="IChannelOutboundClient"/>: ACL-checks
/// the destination and posts a proactive message. Channel posts create a
/// Discord thread; DMs use the root DM message as the session anchor. The
/// session is wired into the actor hierarchy so user replies route back to a
/// live session. Distinct from <see cref="IDiscordOutboundClient"/>, which is
/// the raw Discord API transport this class orchestrates.
/// </summary>
public sealed class DiscordProactiveOutboundClient : IChannelOutboundClient
{
    // The generic send_channel_message tool has no thread-name parameter, so
    // proactive channel posts always create the thread with this default name.
    private const string DefaultThreadName = "Conversation";

    private readonly IDiscordOutboundClient _outboundClient;
    private readonly DiscordChannelOptions _options;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public DiscordProactiveOutboundClient(
        IDiscordOutboundClient outboundClient,
        DiscordChannelOptions options,
        Func<IActorRef?> gatewayAccessor)
    {
        _outboundClient = outboundClient;
        _options = options;
        _gatewayAccessor = gatewayAccessor;
    }

    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Discord);

    public async Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gateway = _gatewayAccessor();
        if (gateway is null)
            return ProactiveSendFormatting.GatewayNotConnected("Discord");

        if (request.AddressKind == ChannelAddressKind.DirectMessage)
            return await SendDirectMessageAsync(request, gateway, ct);

        if (request.AddressKind != ChannelAddressKind.Destination)
            return ProactiveSendFormatting.UnsupportedAddressKind("Discord", request.AddressKind);

        // The default channel is implicitly allowed even when it is absent from
        // AllowedChannelIds, so the ACL check needs it for comparison.
        var defaultChannelId = string.IsNullOrWhiteSpace(_options.DefaultChannelId)
            ? (DiscordChannelId?)null
            : new DiscordChannelId(_options.DefaultChannelId);

        var targetChannelId = new DiscordChannelId(request.TargetId);

        if (!DiscordAclPolicy.IsAllowedChannel(targetChannelId, _options, defaultChannelId))
            return ProactiveSendFormatting.ChannelNotAllowed(targetChannelId.Value);

        DiscordNewThread result;
        try
        {
            result = await _outboundClient.PostNewThreadAsync(targetChannelId, request.Text, DefaultThreadName, ct);
        }
        catch (DiscordThreadCreationFailedException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return $"Message sent to channel {ex.ChannelId.Value}, but Discord could not create a follow-up thread. "
                   + $"Root message: {ex.RootMessageId.Value}. Reason: {detail}";
        }
        catch (Exception ex)
        {
            return ProactiveSendFormatting.PostFailed("Discord", ex.Message);
        }

        var sessionId = SessionIdFormat.Build(result.ChannelId.Value, result.ThreadOrMessageId.Value);
        var target = ProactiveSendFormatting.DescribeTarget(isDirectMessage: false, targetChannelId.Value);

        try
        {
            await gateway.Ask<ProactiveThreadAck>(
                new StartProactiveThread(
                    result.ChannelId,
                    result.ReplyChannelId,
                    result.ThreadOrMessageId,
                    sessionId),
                ProactiveSendFormatting.ProactiveThreadAckTimeout,
                ct);
        }
        catch (Exception)
        {
            // The message was already posted to Discord; only the session
            // pipeline failed to initialize.
            return ProactiveSendFormatting.SentButPipelineFailed(target, sessionId.Value);
        }

        return ProactiveSendFormatting.Sent(target, sessionId.Value);
    }

    private async Task<string> SendDirectMessageAsync(ChannelSendRequest request, IActorRef gateway, CancellationToken ct)
    {
        if (!_options.AllowDirectMessages)
            return ProactiveSendFormatting.DirectMessagesDisabled("Discord");

        var userId = new DiscordUserId(request.TargetId);
        if (!DiscordAclPolicy.IsAllowedUser(userId, _options))
            return ProactiveSendFormatting.UserNotAllowed(userId.Value);

        DiscordNewDirectMessage result;
        try
        {
            result = await _outboundClient.PostDirectMessageAsync(userId, request.Text, ct);
        }
        catch (Exception ex)
        {
            return $"Error: Failed to post direct message to Discord: {ex.Message}";
        }

        var sessionId = SessionIdFormat.Build(result.ChannelId.Value, result.ThreadOrMessageId.Value);
        var target = ProactiveSendFormatting.DescribeTarget(isDirectMessage: true, userId.Value);

        try
        {
            await gateway.Ask<ProactiveThreadAck>(
                new StartProactiveThread(
                    result.ChannelId,
                    result.ReplyChannelId,
                    result.ThreadOrMessageId,
                    sessionId,
                    DirectMessageUserId: result.UserId,
                    RootMessageId: result.RootMessageId),
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
