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
    Grant = "slack")]
public sealed partial class SendSlackMessageTool : NetclawTool<SendSlackMessageTool.Params>
{
    private readonly ISlackOutboundClient _outboundClient;
    private readonly SlackChannelOptions _options;
    private readonly SlackChannelId? _defaultChannelId;
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
        SlackChannelId? defaultChannelId,
        Func<IActorRef?> gatewayAccessor)
    {
        _outboundClient = outboundClient;
        _options = options;
        _defaultChannelId = defaultChannelId;
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
            var userId = new SlackUserId(args.UserId!);

            if (!IsAllowedUser(userId))
                return $"Error: User {userId.Value} is not in the allowed users list.";

            targetChannelId = await _outboundClient.OpenDmChannelAsync(userId, ct);
        }
        else
        {
            targetChannelId = new SlackChannelId(args.ChannelId!);

            if (!IsAllowedChannel(targetChannelId))
                return $"Error: Channel {targetChannelId.Value} is not in the allowed channels list.";
        }

        var result = await _outboundClient.PostNewThreadAsync(targetChannelId, args.Message, ct);

        var sessionId = new SessionId($"{result.ChannelId.Value}/{result.ThreadTs.Value}");
        gateway.Tell(new StartProactiveThread(result.ChannelId, result.ThreadTs, sessionId));

        var target = hasUser ? $"user {args.UserId}" : $"channel {args.ChannelId}";
        return $"Message sent to {target}. Thread: {result.ChannelId.Value}/{result.ThreadTs.Value}";
    }

    private bool IsAllowedUser(SlackUserId userId)
    {
        if (_options.AllowedUserIds.Length == 0)
            return true;

        return _options.AllowedUserIds.Contains(userId.Value, StringComparer.Ordinal);
    }

    private bool IsAllowedChannel(SlackChannelId channelId)
    {
        var matchesDefault = _defaultChannelId is not null
            && channelId == _defaultChannelId.Value;

        var matchesAllowed = _options.AllowedChannelIds
            .Contains(channelId.Value, StringComparer.Ordinal);

        return matchesDefault || matchesAllowed;
    }
}
