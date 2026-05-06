// -----------------------------------------------------------------------
// <copyright file="SendMattermostMessageTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Channels.Mattermost.Tools;

/// <summary>
/// LLM tool that sends a proactive message to a Mattermost channel or DMs a user,
/// creating a new conversation thread. The new thread is wired into the actor
/// hierarchy so user replies route back to a live session.
/// </summary>
[NetclawTool("send_mattermost_message",
    "Send a message to a Mattermost channel or DM a user, creating a new conversation thread. " +
    "Use this to proactively notify users or start discussions. " +
    "Provide exactly one of channel_id or user_id.",
    Grant = "builtin")]
public sealed partial class SendMattermostMessageTool : NetclawTool<SendMattermostMessageTool.Params>, IChannelTool
{
    private readonly IMattermostOutboundClient _outboundClient;
    private readonly MattermostChannelOptions _options;
    private readonly Func<MattermostChannelId?> _defaultChannelIdAccessor;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public record Params(
        [property: Description("The message text to send")]
        string Message,
        [property: Description("Mattermost channel ID to post to. Mutually exclusive with user_id.")]
        string? ChannelId = null,
        [property: Description("Mattermost user ID to DM. Mutually exclusive with channel_id.")]
        string? UserId = null);

    public SendMattermostMessageTool(
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

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Message))
            return "Error: 'message' parameter is required.";

        var hasChannel = !string.IsNullOrWhiteSpace(args.ChannelId);
        var hasUser = !string.IsNullOrWhiteSpace(args.UserId);

        if (hasChannel == hasUser)
            return "Error: Provide exactly one of 'channel_id' or 'user_id'.";

        var gateway = _gatewayAccessor();
        if (gateway is null)
            return "Error: Mattermost gateway is not connected.";

        MattermostChannelId targetChannelId;

        if (hasUser)
        {
            if (!_options.AllowDirectMessages)
                return "Error: Direct messages are disabled. Enable AllowDirectMessages in Mattermost configuration to send DMs.";

            var userId = new MattermostUserId(args.UserId!);

            if (!MattermostAclPolicy.IsAllowedUser(userId, _options))
                return $"Error: User {userId.Value} is not in the allowed users list.";

            try
            {
                targetChannelId = await _outboundClient.OpenDmChannelAsync(userId, ct);
            }
            catch (Exception ex)
            {
                return $"Error: Failed to open DM channel: {ex.Message}";
            }
        }
        else
        {
            targetChannelId = new MattermostChannelId(args.ChannelId!);

            if (!MattermostAclPolicy.IsAllowedChannel(targetChannelId, _options, _defaultChannelIdAccessor()))
                return $"Error: Channel {targetChannelId.Value} is not in the allowed channels list.";
        }

        MattermostNewThread result;
        try
        {
            result = await _outboundClient.PostNewThreadAsync(targetChannelId, args.Message, ct);
        }
        catch (Exception ex)
        {
            return $"Error: Failed to post message to Mattermost: {ex.Message}";
        }

        var sessionId = new SessionId($"{result.ChannelId.Value}/{result.RootPostId.Value}");

        try
        {
            await gateway.Ask<MattermostProactiveThreadAck>(
                new StartMattermostProactiveThread(result.ChannelId, result.RootPostId, sessionId),
                TimeSpan.FromSeconds(30),
                ct);
        }
        catch (Exception)
        {
            var target = hasUser ? $"user {args.UserId}" : $"channel {args.ChannelId}";
            return $"Message sent to {target} but session pipeline failed to initialize. " +
                   $"Thread: {result.ChannelId.Value}/{result.RootPostId.Value}";
        }

        var successTarget = hasUser ? $"user {args.UserId}" : $"channel {args.ChannelId}";
        return $"Message sent to {successTarget}. Thread: {result.ChannelId.Value}/{result.RootPostId.Value}";
    }
}
