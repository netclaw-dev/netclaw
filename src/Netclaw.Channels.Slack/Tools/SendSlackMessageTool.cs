using System.ComponentModel;
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Channels.Slack.Tools;

/// <summary>
/// LLM tool that sends a proactive message to a Slack channel or DMs a user,
/// creating a new conversation thread. The new thread is wired into the actor
/// hierarchy so user replies route back to a live session.
/// </summary>
[NetclawTool("send_slack_message",
    "Send a message to a Slack channel or DM a user, creating a new conversation thread. " +
    "Use this to proactively notify users or start discussions. " +
    "Provide exactly one of channel_id or user_id.",
    Grant = "builtin")]
public sealed partial class SendSlackMessageTool : NetclawTool<SendSlackMessageTool.Params>, IChannelTool
{
    private readonly ISlackOutboundClient _outboundClient;
    private readonly SlackChannelOptions _options;
    private readonly Func<SlackChannelId?> _defaultChannelIdAccessor;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public record Params(
        [property: Description("The message text to send")]
        string Message,
        [property: Description("Slack channel ID (C...) to post to. Mutually exclusive with user_id.")]
        string? ChannelId = null,
        [property: Description("Slack user ID (U...) to DM. Mutually exclusive with channel_id.")]
        string? UserId = null);

    public SendSlackMessageTool(
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

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Message))
            return "Error: 'message' parameter is required.";

        var hasChannel = !string.IsNullOrWhiteSpace(args.ChannelId);
        var hasUser = !string.IsNullOrWhiteSpace(args.UserId);

        if (hasChannel == hasUser) // both set or neither set
            return "Error: Provide exactly one of 'channel_id' or 'user_id'.";

        var gateway = _gatewayAccessor();
        if (gateway is null)
            return "Error: Slack gateway is not connected.";

        SlackChannelId targetChannelId;

        if (hasUser)
        {
            if (!_options.AllowDirectMessages)
                return "Error: Direct messages are disabled. Enable AllowDirectMessages in Slack configuration to send DMs.";

            var userId = new SlackUserId(args.UserId!);

            if (!SlackAclPolicy.IsAllowedUser(userId, _options))
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
            targetChannelId = new SlackChannelId(args.ChannelId!);

            if (!SlackAclPolicy.IsAllowedChannel(targetChannelId, _options, _defaultChannelIdAccessor()))
                return $"Error: Channel {targetChannelId.Value} is not in the allowed channels list.";
        }

        SlackNewThread result;
        try
        {
            result = await _outboundClient.PostNewThreadAsync(targetChannelId, args.Message, ct);
        }
        catch (Exception ex)
        {
            return $"Error: Failed to post message to Slack: {ex.Message}";
        }

        var sessionId = new SessionId($"{result.ChannelId.Value}/{result.ThreadTs.Value}");

        try
        {
            await gateway.Ask<ProactiveThreadAck>(
                new StartProactiveThread(result.ChannelId, result.ThreadTs, sessionId),
                TimeSpan.FromSeconds(30),
                ct);
        }
        catch (Exception)
        {
            // Message was already posted to Slack; the pipeline just didn't initialize
            var target = hasUser ? $"user {args.UserId}" : $"channel {args.ChannelId}";
            return $"Message sent to {target} but session pipeline failed to initialize. " +
                   $"Thread: {result.ChannelId.Value}/{result.ThreadTs.Value}";
        }

        var successTarget = hasUser ? $"user {args.UserId}" : $"channel {args.ChannelId}";
        return $"Message sent to {successTarget}. Thread: {result.ChannelId.Value}/{result.ThreadTs.Value}";
    }
}
