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

        var rootMessage = await textChannel.SendMessageAsync(text: text);

        IThreadChannel thread;
        try
        {
            thread = await textChannel.CreateThreadAsync(
                threadName,
                ThreadType.PublicThread,
                ThreadArchiveDuration.OneDay,
                message: rootMessage);
        }
        catch (HttpException httpEx) when (httpEx.HttpCode == HttpStatusCode.BadRequest)
        {
            // A thread already exists on this message (race with a concurrent
            // post on the same anchor). Reuse the existing thread rather than
            // failing — the proactive message was still delivered.
            var existingThread = await FindExistingThreadAsync(textChannel, rootMessage.Id);
            if (existingThread is null)
                throw new InvalidOperationException(
                    $"Failed to create thread on message {rootMessage.Id} "
                    + "and could not find an existing thread.",
                    httpEx);
            thread = existingThread;
        }

        var threadIdStr = thread.Id.ToString();
        return new DiscordNewThread(
            ChannelId: channelId,
            ReplyChannelId: new DiscordReplyChannelId(threadIdStr),
            ThreadOrMessageId: new DiscordThreadOrMessageId(threadIdStr));
    }

    private static async Task<IThreadChannel?> FindExistingThreadAsync(
        ITextChannel textChannel, ulong anchorMessageId)
    {
        var anchorMsg = await textChannel.GetMessageAsync(anchorMessageId);
        if (anchorMsg?.Thread is { } thread)
            return thread;

        var activeThreads = await textChannel.GetActiveThreadsAsync();
        return activeThreads.FirstOrDefault(t => t.Id == anchorMessageId);
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
