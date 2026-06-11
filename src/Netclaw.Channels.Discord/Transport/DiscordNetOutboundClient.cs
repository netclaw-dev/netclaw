// -----------------------------------------------------------------------
// <copyright file="DiscordNetOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace Netclaw.Channels.Discord.Transport;

internal sealed class DiscordNetOutboundClient : IDiscordOutboundClient
{
    private readonly DiscordSocketClient _client;

    public DiscordNetOutboundClient(DiscordSocketClient client)
    {
        _client = client;
    }

    public async Task<DiscordNewThread> PostNewThreadAsync(
        DiscordChannelId channelId,
        string text,
        string threadName,
        CancellationToken ct = default)
    {
        var channelSnowflake = ParseSnowflake(channelId.Value, "channel ID");
        var channel = await ResolveChannelAsync(channelSnowflake, channelId.Value);

        if (channel is not ITextChannel textChannel)
            throw new InvalidOperationException(
                $"Discord channel {channelId.Value} is not a text channel — "
                + "a proactive post must target a text channel so a thread can be created.");

        var requestOptions = new RequestOptions { CancelToken = ct };
        var rootMessage = await textChannel.SendMessageAsync(text: text, options: requestOptions);

        IThreadChannel thread;
        try
        {
            thread = await textChannel.CreateThreadAsync(
                threadName,
                ThreadType.PublicThread,
                ThreadArchiveDuration.OneDay,
                message: rootMessage,
                options: requestOptions);
        }
        catch (HttpException httpEx) when (httpEx.HttpCode == HttpStatusCode.BadRequest)
        {
            // A thread already exists on this message (race with a concurrent
            // post on the same anchor). Reuse the existing thread rather than
            // failing — the proactive message was still delivered.
            var existingThread = await DiscordThreadHelpers.FindExistingThreadAsync(textChannel, rootMessage.Id);
            if (existingThread is null)
                throw new InvalidOperationException(
                    $"Failed to create thread on message {rootMessage.Id} "
                    + "and could not find an existing thread.",
                    httpEx);
            thread = existingThread;
        }
        catch (Exception ex)
        {
            throw new DiscordThreadCreationFailedException(
                channelId,
                new DiscordMessageId(rootMessage.Id.ToString()),
                $"Discord posted root message {rootMessage.Id} to channel {channelId.Value}, but creating a thread failed.",
                ex);
        }

        var threadIdStr = thread.Id.ToString();
        return new DiscordNewThread(
            ChannelId: channelId,
            ReplyChannelId: new DiscordReplyChannelId(threadIdStr),
            ThreadOrMessageId: new DiscordThreadOrMessageId(threadIdStr));
    }

    public async Task<DiscordNewDirectMessage> PostDirectMessageAsync(
        DiscordUserId userId,
        string text,
        CancellationToken ct = default)
    {
        var userSnowflake = ParseSnowflake(userId.Value, "user ID");
        var requestOptions = new RequestOptions { CancelToken = ct };
        IUser? user = _client.GetUser(userSnowflake);
        user ??= await _client.Rest.GetUserAsync(userSnowflake, requestOptions);

        if (user is null)
            throw new InvalidOperationException($"Discord user {userId.Value} not found.");

        var dmChannel = await user.CreateDMChannelAsync(requestOptions);
        var rootMessage = await dmChannel.SendMessageAsync(text: text, options: requestOptions);
        var dmChannelId = dmChannel.Id.ToString();
        var rootMessageId = rootMessage.Id.ToString();

        return new DiscordNewDirectMessage(
            ChannelId: new DiscordChannelId(dmChannelId),
            ReplyChannelId: new DiscordReplyChannelId(dmChannelId),
            ThreadOrMessageId: new DiscordThreadOrMessageId(rootMessageId),
            RootMessageId: new DiscordMessageId(rootMessageId),
            UserId: userId);
    }

    private async Task<global::Discord.IChannel> ResolveChannelAsync(ulong channelSnowflake, string channelIdForError)
    {
        // Socket cache misses fall back to the REST API, mirroring
        // DiscordNetReplyClient.ResolveMessageChannelAsync.
        global::Discord.IChannel? channel = _client.GetChannel(channelSnowflake);
        channel ??= await _client.Rest.GetChannelAsync(channelSnowflake);

        if (channel is null)
            throw new InvalidOperationException(
                $"Discord channel {channelIdForError} not found.");

        return channel;
    }

    private static ulong ParseSnowflake(string value, string label)
    {
        if (!ulong.TryParse(value, out var id))
            throw new InvalidOperationException($"Discord {label} '{value}' is not a valid snowflake.");
        return id;
    }
}
