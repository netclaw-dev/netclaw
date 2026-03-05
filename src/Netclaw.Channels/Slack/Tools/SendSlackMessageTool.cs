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
    private readonly ISlackReplyClient _replyClient;
    private readonly SlackChannelOptions _options;
    private readonly Func<SlackChannelId?> _defaultChannelIdAccessor;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public record Params(
        [property: Description("The message text to send")]
        string Message,
        [property: Description("Slack channel ID (C...) to post to. Mutually exclusive with user_id.")]
        string? ChannelId = null,
        [property: Description("Slack user ID (U...) to DM. Mutually exclusive with channel_id.")]
        string? UserId = null,
        [property: Description("Comma-separated absolute file paths to attach to the thread. Files must be within the session directory.")]
        string? FilePaths = null);

    public SendSlackMessageTool(
        ISlackOutboundClient outboundClient,
        ISlackReplyClient replyClient,
        SlackChannelOptions options,
        Func<SlackChannelId?> defaultChannelIdAccessor,
        Func<IActorRef?> gatewayAccessor)
    {
        _outboundClient = outboundClient;
        _replyClient = replyClient;
        _options = options;
        _defaultChannelIdAccessor = defaultChannelIdAccessor;
        _gatewayAccessor = gatewayAccessor;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
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

            if (!IsAllowedUser(userId))
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

            if (!IsAllowedChannel(targetChannelId))
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
                TimeSpan.FromSeconds(10),
                ct);
        }
        catch (Exception)
        {
            // Message was already posted to Slack; the pipeline just didn't initialize
            var target = hasUser ? $"user {args.UserId}" : $"channel {args.ChannelId}";
            return $"Message sent to {target} but session pipeline failed to initialize. " +
                   $"Thread: {result.ChannelId.Value}/{result.ThreadTs.Value}";
        }

        // Upload file attachments after the thread is established
        var fileErrors = await UploadFilesAsync(args.FilePaths, context, result.ChannelId, result.ThreadTs, ct);

        var successTarget = hasUser ? $"user {args.UserId}" : $"channel {args.ChannelId}";
        var response = $"Message sent to {successTarget}. Thread: {result.ChannelId.Value}/{result.ThreadTs.Value}";

        if (fileErrors.Count > 0)
            response += $" File upload errors: {string.Join("; ", fileErrors)}";

        return response;
    }

    private async Task<List<string>> UploadFilesAsync(
        string? filePathsCsv,
        ToolExecutionContext context,
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        CancellationToken ct)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(filePathsCsv))
            return errors;

        var sessionDir = context.SessionDirectory;
        var paths = filePathsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawPath in paths)
        {
            var fullPath = Path.GetFullPath(rawPath);

            // Validate file is within session directory (if available)
            if (!string.IsNullOrWhiteSpace(sessionDir) && !IsPathWithinDirectory(fullPath, sessionDir))
            {
                errors.Add($"{rawPath}: path must be within the session directory");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                errors.Add($"{rawPath}: file not found");
                continue;
            }

            try
            {
                await _replyClient.UploadFileToThreadAsync(channelId, threadTs, fullPath, Path.GetFileName(fullPath), ct);
            }
            catch (Exception ex)
            {
                errors.Add($"{rawPath}: {ex.Message}");
            }
        }

        return errors;
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDir = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!fullPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
            return false;

        if (fullPath.Length == normalizedDir.Length)
            return true;

        var boundary = fullPath[normalizedDir.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    private bool IsAllowedUser(SlackUserId userId)
    {
        if (_options.AllowedUserIds.Length == 0)
            return true;

        return _options.AllowedUserIds.Contains(userId.Value, StringComparer.Ordinal);
    }

    private bool IsAllowedChannel(SlackChannelId channelId)
    {
        var defaultChannelId = _defaultChannelIdAccessor();
        var matchesDefault = defaultChannelId is not null
            && channelId == defaultChannelId.Value;

        var matchesAllowed = _options.AllowedChannelIds
            .Contains(channelId.Value, StringComparer.Ordinal);

        return matchesDefault || matchesAllowed;
    }
}
