// -----------------------------------------------------------------------
// <copyright file="DiscordNetReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace Netclaw.Channels.Discord.Transport;

internal sealed class DiscordNetReplyClient : IDiscordReplyClient
{
    private const int MaxRestChannelCacheSize = 1000;
    private static readonly MessageComponent EmptyComponents = new ComponentBuilder().Build();

    private readonly DiscordSocketClient _client;
    private readonly ConcurrentDictionary<ulong, IMessageChannel> _restChannelCache = new();

    public DiscordNetReplyClient(DiscordSocketClient client)
    {
        _client = client;
    }

    public async Task<DiscordPostResult> PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default)
    {
        var channelId = ParseSnowflake(message.ReplyChannelId.Value, "reply channel ID");
        var messageChannel = await ResolveMessageChannelAsync(channelId, message.ReplyChannelId.Value);

        IMessageChannel targetChannel = messageChannel;
        DiscordReplyChannelId? createdThreadId = null;

        if (message.CreateThreadOnMessage is { } threadAnchor
            && messageChannel is ITextChannel textChannel)
        {
            var anchorId = ParseSnowflake(threadAnchor.Value, "anchor message ID");
            var threadName = message.ThreadName ?? "Conversation";
            var anchorMessage = await textChannel.GetMessageAsync(anchorId)
                ?? throw new InvalidOperationException(
                    $"Discord message {threadAnchor.Value} not found — cannot create thread.");
            IThreadChannel thread;
            try
            {
                thread = await textChannel.CreateThreadAsync(
                    threadName,
                    ThreadType.PublicThread,
                    ThreadArchiveDuration.OneDay,
                    message: anchorMessage);
            }
            catch (HttpException httpEx) when (httpEx.HttpCode == HttpStatusCode.BadRequest)
            {
                // Thread already exists on this message (race between two simultaneous
                // messages). Try to find and use the existing thread.
                var existingThread = await DiscordThreadHelpers.FindExistingThreadAsync(textChannel, anchorId);
                if (existingThread is null)
                    throw new InvalidOperationException(
                        $"Failed to create thread on message {threadAnchor.Value} and could not find existing thread.",
                        httpEx);
                thread = existingThread;
            }
            targetChannel = thread;
            createdThreadId = new DiscordReplyChannelId(thread.Id.ToString());
        }

        MessageComponent? components = null;
        if (message.Buttons is { Count: > 0 })
        {
            var builder = new ComponentBuilder();
            foreach (var button in message.Buttons)
            {
                builder.WithButton(
                    label: button.Label,
                    customId: button.CustomId,
                    style: (ButtonStyle)(int)button.Style);
            }

            components = builder.Build();
        }

        MessageReference? rootRef = null;
        if (message.CreateThreadOnMessage is null && message.RootMessageId is { } rootId)
            rootRef = new MessageReference(ParseSnowflake(rootId.Value, "root message ID"));

        var sentMessage = await targetChannel.SendMessageAsync(
            text: message.Text,
            messageReference: rootRef,
            components: components);

        var sentMessageId = sentMessage is not null
            ? new DiscordMessageId(sentMessage.Id.ToString())
            : (DiscordMessageId?)null;

        return new DiscordPostResult(CreatedThreadId: createdThreadId, MessageId: sentMessageId);
    }

    public async Task SetThreadNameAsync(DiscordReplyChannelId threadChannelId, string name, CancellationToken cancellationToken = default)
    {
        var channelId = ParseSnowflake(threadChannelId.Value, "thread channel ID");
        var channel = _client.GetChannel(channelId);
        if (channel is not IThreadChannel thread)
            return;

        await thread.ModifyAsync(props =>
        {
            props.Name = name.Length > 100 ? name[..100] : name;
        }, new RequestOptions { CancelToken = cancellationToken });
    }

    public async Task UpdateMessageAsync(
        DiscordReplyChannelId channelId,
        DiscordMessageId messageId,
        string text,
        bool removeComponents = false,
        CancellationToken cancellationToken = default)
    {
        var channelSnowflake = ParseSnowflake(channelId.Value, "reply channel ID");
        var messageSnowflake = ParseSnowflake(messageId.Value, "message ID");
        var messageChannel = await ResolveMessageChannelAsync(channelSnowflake, channelId.Value);

        await messageChannel.ModifyMessageAsync(messageSnowflake, props =>
        {
            props.Content = text;
            if (removeComponents)
                props.Components = EmptyComponents;
        }, new RequestOptions { CancelToken = cancellationToken });
    }

    public async Task TriggerTypingAsync(DiscordReplyChannelId channelId, CancellationToken cancellationToken = default)
    {
        var channelSnowflake = ParseSnowflake(channelId.Value, "reply channel ID");
        var messageChannel = await ResolveMessageChannelAsync(channelSnowflake, channelId.Value);

        await messageChannel.TriggerTypingAsync(new RequestOptions { CancelToken = cancellationToken });
    }

    public async Task<DiscordMessageId?> UploadFileAsync(DiscordFileUpload upload, CancellationToken cancellationToken = default)
    {
        var channelSnowflake = ParseSnowflake(upload.ReplyChannelId.Value, "reply channel ID");
        var messageChannel = await ResolveMessageChannelAsync(channelSnowflake, upload.ReplyChannelId.Value);
        MessageReference? rootRef = null;
        if (upload.RootMessageId is { } rootMessageId)
            rootRef = new MessageReference(ParseSnowflake(rootMessageId.Value, "root message ID"));

        await using var stream = File.OpenRead(upload.FilePath);
        var sentMessage = await messageChannel.SendFileAsync(
            stream,
            upload.FileName,
            text: upload.Text,
            options: new RequestOptions { CancelToken = cancellationToken },
            messageReference: rootRef);

        return sentMessage is null
            ? null
            : new DiscordMessageId(sentMessage.Id.ToString());
    }

    private async Task<IMessageChannel> ResolveMessageChannelAsync(ulong channelSnowflake, string channelIdForError)
    {
        // Socket cache misses for DM channels — fall back to REST API.
        IMessageChannel? channel = _client.GetChannel(channelSnowflake) as IMessageChannel;
        if (channel is null && !_restChannelCache.TryGetValue(channelSnowflake, out channel))
        {
            var restChannel = await _client.Rest.GetChannelAsync(channelSnowflake);
            channel = restChannel as IMessageChannel;
            if (channel is not null)
            {
                if (_restChannelCache.Count >= MaxRestChannelCacheSize)
                    _restChannelCache.Clear();
                _restChannelCache[channelSnowflake] = channel;
            }
        }

        if (channel is null)
            throw new InvalidOperationException(
                $"Discord channel {channelIdForError} not found or is not a message channel.");

        return channel;
    }

    private static ulong ParseSnowflake(string value, string label)
    {
        if (!ulong.TryParse(value, out var id))
            throw new InvalidOperationException($"Discord {label} '{value}' is not a valid snowflake.");
        return id;
    }
}
