// -----------------------------------------------------------------------
// <copyright file="SendDiscordMessageTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Channels.Discord.Tools;

/// <summary>
/// LLM tool that posts a proactive message to a Discord channel, creating a new
/// conversation thread. The thread is wired into the actor hierarchy so user
/// replies route back to a live session. Channel targets only — DM proactive
/// posting is deferred (see the add-discord-proactive-post OpenSpec change).
/// </summary>
[NetclawTool("send_discord_message",
    "Send a message to a Discord channel, creating a new conversation thread. " +
    "Use this to proactively notify users or start discussions. " +
    "Omit channel_id to use the configured default channel.",
    Grant = "builtin")]
public sealed partial class SendDiscordMessageTool : NetclawTool<SendDiscordMessageTool.Params>, IChannelTool
{
    private const int MaxThreadNameLength = 100;

    private readonly IDiscordOutboundClient _outboundClient;
    private readonly DiscordChannelOptions _options;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public record Params(
        [property: Description("The message text to send")]
        string Message,
        [property: Description("Discord channel ID to post to. Defaults to the configured default channel if omitted.")]
        string? ChannelId = null,
        [property: Description("Optional name for the conversation thread created on the message.")]
        string? ThreadName = null);

    public SendDiscordMessageTool(
        IDiscordOutboundClient outboundClient,
        DiscordChannelOptions options,
        Func<IActorRef?> gatewayAccessor)
    {
        _outboundClient = outboundClient;
        _options = options;
        _gatewayAccessor = gatewayAccessor;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Message))
            return "Error: 'message' parameter is required.";

        var gateway = _gatewayAccessor();
        if (gateway is null)
            return "Error: Discord gateway is not connected.";

        var defaultChannelId = string.IsNullOrWhiteSpace(_options.DefaultChannelId)
            ? (DiscordChannelId?)null
            : new DiscordChannelId(_options.DefaultChannelId);

        var channelIdValue = !string.IsNullOrWhiteSpace(args.ChannelId)
            ? args.ChannelId!
            : defaultChannelId?.Value;

        if (string.IsNullOrWhiteSpace(channelIdValue))
            return "Error: No 'channel_id' provided and no default Discord channel is configured.";

        var targetChannelId = new DiscordChannelId(channelIdValue);

        if (!DiscordAclPolicy.IsAllowedChannel(targetChannelId, _options, defaultChannelId))
            return $"Error: Channel {targetChannelId.Value} is not in the allowed channels list.";

        var threadName = "Conversation";
        if (!string.IsNullOrWhiteSpace(args.ThreadName))
        {
            threadName = args.ThreadName!.Length > MaxThreadNameLength
                ? args.ThreadName![..MaxThreadNameLength]
                : args.ThreadName!;
        }

        DiscordNewThread result;
        try
        {
            result = await _outboundClient.PostNewThreadAsync(targetChannelId, args.Message, threadName, ct);
        }
        catch (DiscordThreadCreationFailedException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return $"Message sent to channel {ex.ChannelId.Value}, but Discord could not create a follow-up thread. "
                   + $"Root message: {ex.RootMessageId.Value}. Reason: {detail}";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to post message to Discord: {ex.Message}";
        }

        var sessionId = new SessionId($"{result.ChannelId.Value}/{result.ThreadOrMessageId.Value}");

        try
        {
            await gateway.Ask<ProactiveThreadAck>(
                new StartProactiveThread(
                    result.ChannelId,
                    result.ReplyChannelId,
                    result.ThreadOrMessageId,
                    sessionId),
                TimeSpan.FromSeconds(30),
                ct);
        }
        catch (Exception)
        {
            // The message was already posted to Discord; only the session
            // pipeline failed to initialize.
            return $"Message sent to channel {targetChannelId.Value} but session pipeline failed to initialize. " +
                   $"Thread: {sessionId.Value}";
        }

        return $"Message sent to channel {targetChannelId.Value}. Thread: {sessionId.Value}";
    }
}
