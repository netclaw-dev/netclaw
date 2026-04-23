using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Discord.Transport;

internal sealed class DiscordNetGatewayClient : IDiscordGatewayClient
{
    private readonly DiscordSocketClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DiscordNetGatewayClient> _logger;

    public event Func<DiscordGatewayMessage, Task>? MessageReceived;
    public event Func<DiscordGatewayInteraction, Task>? InteractionReceived;

    public bool IsConnected => _client.ConnectionState == ConnectionState.Connected;

    public DiscordNetGatewayClient(
        DiscordSocketClient client,
        TimeProvider timeProvider,
        ILogger<DiscordNetGatewayClient> logger)
    {
        _client = client;
        _timeProvider = timeProvider;
        _logger = logger;

        _client.Log += OnDiscordLog;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.ButtonExecuted += OnButtonExecutedAsync;
    }

    public async Task ConnectAsync(string botToken, CancellationToken cancellationToken = default)
    {
        await _client.LoginAsync(TokenType.Bot, botToken);
        await _client.StartAsync();
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message is not SocketUserMessage)
            return;

        var handler = MessageReceived;
        if (handler is null)
            return;

        var (channelId, replyChannelId, threadOrMessageId) = ResolveChannelContext(
            message.Channel, message.Id);

        var isThread = message.Channel is SocketThreadChannel;
        var messageIdStr = message.Id.ToString();

        var gatewayMessage = new DiscordGatewayMessage(
            EventId: new DiscordEventId(messageIdStr),
            ChannelId: new DiscordChannelId(channelId),
            ReplyChannelId: new DiscordReplyChannelId(replyChannelId),
            MessageId: new DiscordMessageId(messageIdStr),
            ThreadOrMessageId: new DiscordThreadOrMessageId(threadOrMessageId),
            RootMessageId: isThread ? null : new DiscordMessageId(messageIdStr),
            SenderId: new DiscordUserId(message.Author.Id.ToString()),
            IsBotMessage: message.Author.IsBot,
            IsDirectMessage: message.Channel is IDMChannel,
            Text: message.Content,
            ReceivedAt: _timeProvider.GetUtcNow());

        try
        {
            await handler(gatewayMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Discord message {MessageId}", message.Id);
        }
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        try
        {
            await component.DeferAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to defer Discord button interaction {InteractionId}", component.Id);
            return;
        }

        var handler = InteractionReceived;
        if (handler is null)
            return;

        if (!ApprovalButtonValueCodec.TryDecode(
                component.Data.CustomId,
                out var callId,
                out var selectedKey,
                out var requesterSenderId))
        {
            _logger.LogWarning("Failed to parse button custom ID: {CustomId}", component.Data.CustomId);
            return;
        }

        var (channelId, _, threadOrMessageId) = ResolveChannelContext(
            component.Channel, component.Message.Id);

        var interaction = new DiscordGatewayInteraction(
            ChannelId: new DiscordChannelId(channelId),
            ThreadOrMessageId: new DiscordThreadOrMessageId(threadOrMessageId),
            CallId: callId!,
            SelectedKey: selectedKey!,
            SenderId: new DiscordUserId(component.User.Id.ToString()),
            RequesterSenderId: requesterSenderId is not null
                ? new DiscordUserId(requesterSenderId)
                : null,
            ReceivedAt: _timeProvider.GetUtcNow());

        try
        {
            await handler(interaction);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Discord button interaction {InteractionId}", component.Id);
        }
    }

    private static (string ChannelId, string ReplyChannelId, string ThreadOrMessageId) ResolveChannelContext(
        ISocketMessageChannel channel, ulong fallbackMessageId)
    {
        var replyChannelId = channel.Id.ToString();

        if (channel is SocketThreadChannel thread)
            return (thread.ParentChannel.Id.ToString(), replyChannelId, channel.Id.ToString());

        return (channel.Id.ToString(), replyChannelId, fallbackMessageId.ToString());
    }

    private Task OnDiscordLog(LogMessage logMessage)
    {
        var level = logMessage.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        _logger.Log(level, logMessage.Exception, "[Discord.Net] {Source}: {Message}",
            logMessage.Source, logMessage.Message);

        return Task.CompletedTask;
    }
}
