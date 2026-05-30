// -----------------------------------------------------------------------
// <copyright file="DiscordNetGatewayClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Discord.Transport;

internal sealed class DiscordNetGatewayClient : IDiscordGatewayClient, IDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DiscordNetGatewayClient> _logger;

    // Reassigned per ConnectAsync call so the channel can retry a transient
    // failure with a fresh readiness signal. Read by Discord.Net event threads.
    private volatile TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 0/1 guard: Discord.Net raises Disconnected on every reconnect attempt,
    // so a fatal close is logged and acted on exactly once. Reset per ConnectAsync.
    private int _fatalCloseHandled;
    private int _hasObservedReady;
    private int _waitingForConnectReady;
    private int _cleanReconnectRequested;

    public event Func<DiscordGatewayMessage, Task>? MessageReceived;
    public event Func<DiscordGatewayInteraction, Task>? InteractionReceived;
    public event Func<string, Task>? CleanReconnectRequired;

    private volatile string? _botMentionTag;
    private volatile string? _healthDetail = "Discord gateway disconnected.";

    public bool IsConnected => _client.ConnectionState == ConnectionState.Connected;
    public bool IsReady => IsConnected
        && Volatile.Read(ref _hasObservedReady) == 1
        && BotUserId is not null
        && _botMentionTag is not null
        && Volatile.Read(ref _cleanReconnectRequested) == 0;

    public string? HealthDetail => IsReady
        ? null
        : _healthDetail ?? "Discord gateway is not ready.";

    public DiscordUserId? BotUserId { get; private set; }

    public DiscordNetGatewayClient(
        DiscordSocketClient client,
        TimeProvider timeProvider,
        ILogger<DiscordNetGatewayClient> logger)
    {
        _client = client;
        _timeProvider = timeProvider;
        _logger = logger;

        _client.Log += OnDiscordLog;
        _client.Connected += OnConnectedAsync;
        _client.Ready += OnReadyAsync;
        _client.Disconnected += OnDisconnectedAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.ButtonExecuted += OnButtonExecutedAsync;
    }

    public async Task ConnectAsync(string botToken, CancellationToken cancellationToken = default)
    {
        // Fresh readiness signal per attempt so a retry after a transient
        // failure is not satisfied by a stale result or fault.
        var readyTcs = _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _fatalCloseHandled, 0);
        Interlocked.Exchange(ref _hasObservedReady, 0);
        Interlocked.Exchange(ref _waitingForConnectReady, 1);
        Interlocked.Exchange(ref _cleanReconnectRequested, 0);
        _healthDetail = "Discord gateway connecting.";

        await _client.LoginAsync(TokenType.Bot, botToken);
        await _client.StartAsync();

        // Wait for the READY event so that CurrentUser is populated before
        // we start processing messages. Without this, BotUserId and
        // _botMentionTag would be null and mention detection would fail.
        // OnDisconnectedAsync faults this task on a fatal close (e.g. disallowed
        // intents), so a misconfiguration fails fast instead of hitting the timeout.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(30));
        await readyTcs.Task.WaitAsync(linkedCts.Token);
    }

    private Task OnReadyAsync()
    {
        if (!TryRefreshBotIdentity("READY"))
        {
            var failure = new ChannelConnectException(
                ChannelConnectFailureKind.Transient,
                "Discord gateway reached READY, but Discord.Net did not expose the current bot identity.");
            _healthDetail = failure.Message;
            _readyTcs.TrySetException(failure);
            return Task.CompletedTask;
        }

        Interlocked.Exchange(ref _hasObservedReady, 1);
        Interlocked.Exchange(ref _waitingForConnectReady, 0);
        _healthDetail = null;
        _readyTcs.TrySetResult();
        return Task.CompletedTask;
    }

    private Task OnConnectedAsync()
    {
        if (Volatile.Read(ref _waitingForConnectReady) == 1)
        {
            _healthDetail = "Discord gateway connected; waiting for READY.";
            return Task.CompletedTask;
        }

        return RequestCleanReconnectAsync(
            "Discord gateway reconnected outside a clean startup cycle; forcing a clean reconnect.");
    }

    private Task OnDisconnectedAsync(Exception exception)
    {
        Interlocked.Exchange(ref _hasObservedReady, 0);
        _healthDetail = "Discord gateway disconnected.";

        // Transient drops are left alone — Discord.Net reconnects on its own,
        // and OnReadyAsync completes the readiness signal when it recovers.
        var classified = DiscordConnectFailureClassifier.Classify(exception);
        if (!classified.IsFatal)
            return Task.CompletedTask;

        // Surface the fatal close to ConnectAsync immediately instead of
        // letting the caller block on the 30s readiness timeout.
        _readyTcs.TrySetException(classified);

        // Discord.Net raises Disconnected on every reconnect attempt. A fatal
        // close (bad token, disallowed/invalid intents) will never recover on
        // its own, so handle it exactly once: log it, then stop the client so
        // Discord.Net does not retry a configuration error forever — the
        // channel has already torn down and would not pick the socket back up,
        // so further retries are pure churn and log spam.
        if (Interlocked.Exchange(ref _fatalCloseHandled, 1) == 1)
            return Task.CompletedTask;

        _logger.LogError(classified, "Discord gateway closed fatally: {Reason}", classified.Message);

        _ = Task.Run(async () =>
        {
            try
            {
                await _client.StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error stopping Discord client after fatal close.");
            }
        });

        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _healthDetail = "Discord gateway disconnecting.";
        await _client.StopAsync();
        await _client.LogoutAsync();
        _healthDetail = "Discord gateway disconnected.";
    }

    public void Dispose()
    {
        _client.Log -= OnDiscordLog;
        _client.Connected -= OnConnectedAsync;
        _client.Ready -= OnReadyAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.ButtonExecuted -= OnButtonExecutedAsync;
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message is not SocketUserMessage userMessage)
            return;

        if (!IsReady)
        {
            _logger.LogWarning(
                "Dropping Discord message {MessageId} while gateway is not ready: {Reason}",
                message.Id,
                HealthDetail);
            return;
        }

        var handler = MessageReceived;
        if (handler is null)
            return;

        var (channelId, replyChannelId, threadOrMessageId) = ResolveChannelContext(
            message.Channel, message.Id);

        var isThread = message.Channel is SocketThreadChannel;
        var isDm = message.Channel is IDMChannel;
        var messageIdStr = message.Id.ToString();

        var containsMention = _botMentionTag is not null
            && userMessage.Content.Contains(_botMentionTag, StringComparison.Ordinal);

        IReadOnlyList<DiscordFileReference>? attachments = null;
        if (message.Attachments.Count > 0)
        {
            attachments = message.Attachments
                .Select(a => new DiscordFileReference(
                    Name: a.Filename,
                    MimeType: a.ContentType ?? "application/octet-stream",
                    Size: (long)a.Size,
                    Url: a.Url))
                .ToList();
        }

        var gatewayMessage = new DiscordGatewayMessage(
            EventId: new DiscordEventId(messageIdStr),
            ChannelId: new DiscordChannelId(channelId),
            ReplyChannelId: new DiscordReplyChannelId(replyChannelId),
            MessageId: new DiscordMessageId(messageIdStr),
            ThreadOrMessageId: new DiscordThreadOrMessageId(threadOrMessageId),
            RootMessageId: isThread || isDm ? null : new DiscordMessageId(messageIdStr),
            SenderId: new DiscordUserId(message.Author.Id.ToString()),
            IsBotMessage: message.Author.IsBot,
            IsDirectMessage: isDm,
            ContainsBotMention: containsMention,
            Text: message.Content,
            ReceivedAt: _timeProvider.GetUtcNow(),
            Attachments: attachments,
            IsInThread: isThread);

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
        if (!IsReady)
        {
            _logger.LogWarning(
                "Dropping Discord interaction {InteractionId} while gateway is not ready: {Reason}",
                component.Id,
                HealthDetail);
            return;
        }

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

        var (channelId, replyChannelId, threadOrMessageId) = ResolveChannelContext(
            component.Channel, component.Message.Id);

        // The clicked message's ID is the prompt we need to update on resolution
        // — survives passivation so we can redraw even on a cold-spawned binding.
        // See issue #939.
        var promptMessageId = new DiscordMessageId(component.Message.Id.ToString());

        var interaction = new DiscordGatewayInteraction(
            ChannelId: new DiscordChannelId(channelId),
            ThreadOrMessageId: new DiscordThreadOrMessageId(threadOrMessageId),
            CallId: callId!,
            SelectedKey: selectedKey!,
            SenderId: new DiscordUserId(component.User.Id.ToString()),
            RequesterSenderId: requesterSenderId is not null
                ? new DiscordUserId(requesterSenderId)
                : null,
            ReceivedAt: _timeProvider.GetUtcNow(),
            PromptMessageId: promptMessageId,
            // Explicit reply channel ID. For top-level guild prompts the third
            // tuple slot (ThreadOrMessageId) is the *message* ID, so it cannot
            // double as a channel ID for chat.update. See issue #939.
            ReplyChannelId: new DiscordReplyChannelId(replyChannelId));

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
        if (channel is SocketThreadChannel thread)
            return ResolveChannelContext(channel.Id, fallbackMessageId, DiscordChannelKind.Thread, thread.ParentChannel.Id);

        var kind = channel is IDMChannel ? DiscordChannelKind.DirectMessage : DiscordChannelKind.GuildChannel;
        return ResolveChannelContext(channel.Id, fallbackMessageId, kind, parentChannelId: null);
    }

    internal static (string ChannelId, string ReplyChannelId, string ThreadOrMessageId) ResolveChannelContext(
        ulong channelId, ulong messageId,
        DiscordChannelKind kind,
        ulong? parentChannelId)
    {
        var channelIdStr = channelId.ToString();

        return kind switch
        {
            DiscordChannelKind.Thread when parentChannelId is not null =>
                (parentChannelId.Value.ToString(), channelIdStr, channelIdStr),
            // DMs use the channel ID as the session key so all messages from
            // one user share a single long-running session.
            DiscordChannelKind.DirectMessage =>
                (channelIdStr, channelIdStr, channelIdStr),
            _ =>
                (channelIdStr, channelIdStr, messageId.ToString()),
        };
    }

    internal enum DiscordChannelKind
    {
        GuildChannel,
        Thread,
        DirectMessage,
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

        if (string.Equals(logMessage.Source, "Gateway", StringComparison.Ordinal)
            && logMessage.Message?.Contains("Resumed previous session", StringComparison.OrdinalIgnoreCase) == true)
        {
            return RequestCleanReconnectAsync(
                "Discord.Net resumed a previous gateway session; forcing a clean reconnect to avoid stale resumed state.");
        }

        return Task.CompletedTask;
    }

    private bool TryRefreshBotIdentity(string source)
    {
        if (_client.CurrentUser is not { } currentUser)
        {
            _healthDetail = "Discord gateway is connected but the current bot identity is unavailable.";
            return false;
        }

        var botUserId = new DiscordUserId(currentUser.Id.ToString());
        var previousBotUserId = BotUserId;
        BotUserId = botUserId;
        _botMentionTag = $"<@{currentUser.Id}>";

        if (previousBotUserId == botUserId)
        {
            _logger.LogDebug("Discord bot identity refreshed from {Source}: {BotUserId}", source, currentUser.Id);
        }
        else if (string.Equals(source, "READY", StringComparison.Ordinal))
        {
            _logger.LogInformation("Discord bot identity resolved: {BotUserId}", currentUser.Id);
        }
        else
        {
            _logger.LogInformation("Discord bot identity resolved from {Source}: {BotUserId}", source, currentUser.Id);
        }

        return true;
    }

    private async Task RequestCleanReconnectAsync(string reason)
    {
        _healthDetail = reason;

        if (Interlocked.Exchange(ref _cleanReconnectRequested, 1) == 1)
            return;

        _logger.LogWarning("Discord gateway requested clean reconnect: {Reason}", reason);

        var handler = CleanReconnectRequired;
        if (handler is null)
            return;

        try
        {
            await handler(reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord clean reconnect handler failed.");
        }
    }
}
