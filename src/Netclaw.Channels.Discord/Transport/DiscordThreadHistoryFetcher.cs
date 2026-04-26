using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Channels.Discord.Transport;

public sealed class DiscordThreadHistoryFetcher : IThreadHistoryFetcher
{
    private const int MaxMessages = 200;

    private readonly DiscordSocketClient _client;
    private readonly ILogger<DiscordThreadHistoryFetcher> _logger;

    public DiscordThreadHistoryFetcher(
        DiscordSocketClient client,
        ILogger<DiscordThreadHistoryFetcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var parts = sessionId.Value.Split('/', 2);
        if (parts.Length != 2)
        {
            _logger.LogWarning("Cannot extract channel/thread from session ID {SessionId}", sessionId.Value);
            return [];
        }

        if (!ulong.TryParse(parts[1], out var threadChannelId))
        {
            _logger.LogWarning("Thread portion of session ID is not a valid snowflake: {SessionId}", sessionId.Value);
            return [];
        }

        try
        {
            return await FetchMessagesAsync(threadChannelId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch thread history for {SessionId}", sessionId.Value);
            return [];
        }
    }

    private async Task<IReadOnlyList<ChannelInput>> FetchMessagesAsync(
        ulong threadChannelId,
        CancellationToken cancellationToken)
    {
        var channel = _client.GetChannel(threadChannelId) as IMessageChannel;
        if (channel is null)
        {
            _logger.LogWarning("Discord channel {ChannelId} not found or is not a message channel", threadChannelId);
            return [];
        }

        var results = new List<ChannelInput>();

        // Discord threads are separate channels — GetMessagesAsync only returns
        // messages posted IN the thread, not the parent message that started it.
        // The thread channel ID equals the parent message's snowflake, so fetch
        // that message from the parent channel and prepend it to the history.
        if (channel is SocketThreadChannel threadChannel)
        {
            var parentChannel = threadChannel.ParentChannel as IMessageChannel;
            if (parentChannel is not null)
            {
                try
                {
                    var rootMessage = await parentChannel.GetMessageAsync(
                        threadChannelId,
                        options: new RequestOptions { CancelToken = cancellationToken });

                    if (rootMessage is not null && !rootMessage.Author.IsBot
                        && !string.IsNullOrWhiteSpace(rootMessage.Content))
                    {
                        results.Add(ToChannelInput(rootMessage, threadChannelId));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to fetch root message for thread {ThreadId}", threadChannelId);
                }
            }
        }

        var messages = await channel
            .GetMessagesAsync(MaxMessages, options: new RequestOptions { CancelToken = cancellationToken })
            .FlattenAsync();

        foreach (var message in messages.OrderBy(m => m.Timestamp))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (message.Author.IsBot)
                continue;

            if (string.IsNullOrWhiteSpace(message.Content))
                continue;

            results.Add(ToChannelInput(message, threadChannelId));
        }

        _logger.LogInformation("Fetched {Count} thread history messages for thread {ThreadId}", results.Count, threadChannelId);
        return results;
    }

    private static ChannelInput ToChannelInput(IMessage message, ulong threadChannelId) => new()
    {
        SenderId = message.Author.Id.ToString(),
        ChannelId = threadChannelId.ToString(),
        MessageId = message.Id.ToString(),
        Audience = TrustAudience.Public,
        Principal = PrincipalClassification.UntrustedExternal,
        Provenance = new SourceProvenance
        {
            TransportAuthenticity = TransportAuthenticity.Verified,
            PayloadTaint = PayloadTaint.Public,
            SourceKind = "discord",
            SourceScope = threadChannelId.ToString()
        },
        Contents = [new TextContent(message.Content)],
        ReceivedAt = message.Timestamp
    };
}
