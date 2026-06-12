// -----------------------------------------------------------------------
// <copyright file="DiscordNetGatewayLifecycleActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Discord.Transport;

internal interface IDiscordGatewayEventSink
{
    Task PublishMessageAsync(DiscordGatewayMessage message);

    Task PublishInteractionAsync(DiscordGatewayInteraction interaction);

    Task PublishCleanReconnectRequiredAsync(string reason);

    Task PublishConnectionRestoredAsync(DiscordGatewaySnapshot snapshot);
}

internal interface IDiscordGatewayTransport
{
    event Func<LogMessage, Task> Log;

    event Func<Task> Connected;

    event Func<Task> Ready;

    event Func<Exception, Task> Disconnected;

    event Func<SocketMessage, Task> MessageReceived;

    event Func<SocketMessageComponent, Task> ButtonExecuted;

    ConnectionState ConnectionState { get; }

    ulong? CurrentUserId { get; }

    Task LoginAsync(string botToken);

    Task StartAsync();

    Task StopAsync();

    Task LogoutAsync();
}

internal sealed class DiscordSocketGatewayTransport(DiscordSocketClient client) : IDiscordGatewayTransport
{
    public event Func<LogMessage, Task> Log
    {
        add => client.Log += value;
        remove => client.Log -= value;
    }

    public event Func<Task> Connected
    {
        add => client.Connected += value;
        remove => client.Connected -= value;
    }

    public event Func<Task> Ready
    {
        add => client.Ready += value;
        remove => client.Ready -= value;
    }

    public event Func<Exception, Task> Disconnected
    {
        add => client.Disconnected += value;
        remove => client.Disconnected -= value;
    }

    public event Func<SocketMessage, Task> MessageReceived
    {
        add => client.MessageReceived += value;
        remove => client.MessageReceived -= value;
    }

    public event Func<SocketMessageComponent, Task> ButtonExecuted
    {
        add => client.ButtonExecuted += value;
        remove => client.ButtonExecuted -= value;
    }

    public ConnectionState ConnectionState => client.ConnectionState;

    public ulong? CurrentUserId => client.CurrentUser?.Id;

    public Task LoginAsync(string botToken) => client.LoginAsync(TokenType.Bot, botToken);

    public Task StartAsync() => client.StartAsync();

    public Task StopAsync() => client.StopAsync();

    public Task LogoutAsync() => client.LogoutAsync();
}

/// <summary>
/// Discord gateway lifecycle: the shared state machine lives in
/// <see cref="ChannelLifecycleActor{TSnapshot,TConnectCommand}"/>. Discord's
/// start (login + StartAsync) does not imply readiness — the actor stays in
/// Connecting until Discord.Net raises an explicit READY event, guarded by a
/// 30-second ready timeout. Fatal gateway closes (bad token, missing intents)
/// disable auto-reconnect and stop the socket client.
/// </summary>
internal sealed class DiscordNetGatewayLifecycleActor :
    ChannelLifecycleActor<DiscordGatewaySnapshot, DiscordNetGatewayLifecycleActor.Connect>
{
    private readonly IDiscordGatewayTransport _client;
    private readonly IDiscordGatewayEventSink _eventSink;

    private DiscordUserId? _botUserId;
    private string? _botMentionTag;

    // Set only by HandleFatalClose, which lands in Disconnected with
    // auto-reconnect cancelled; the only way back out is an operator connect,
    // which resets it via ResetIdentityState. So it is always false on any
    // transition to Ready.
    private bool _fatalCloseHandled;

    public DiscordNetGatewayLifecycleActor(
        IDiscordGatewayTransport client,
        TimeProvider timeProvider,
        IDiscordGatewayEventSink eventSink,
        ILogger logger)
        : base("Discord", timeProvider, logger)
    {
        _client = client;
        _eventSink = eventSink;
    }

    public static Props CreateProps(
        IDiscordGatewayTransport client,
        TimeProvider timeProvider,
        IDiscordGatewayEventSink eventSink,
        ILogger logger) =>
        Props.Create(() => new DiscordNetGatewayLifecycleActor(client, timeProvider, eventSink, logger));

    /// <summary>Discord's start does not imply readiness; wait up to 30s for Discord.Net's READY event.</summary>
    protected override TimeSpan? ReadySignalTimeout => TimeSpan.FromSeconds(30);

    protected override bool IsTransportConnected => _client.ConnectionState == ConnectionState.Connected;

    protected override bool HasBotIdentity => _botUserId is not null && _botMentionTag is not null;

    protected override void SubscribeTransportEvents()
    {
        _client.Log += OnDiscordLogAsync;
        _client.Connected += OnConnectedAsync;
        _client.Ready += OnReadyAsync;
        _client.Disconnected += OnDisconnectedAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.ButtonExecuted += OnButtonExecutedAsync;
    }

    protected override void UnsubscribeTransportEvents()
    {
        _client.Log -= OnDiscordLogAsync;
        _client.Connected -= OnConnectedAsync;
        _client.Ready -= OnReadyAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.ButtonExecuted -= OnButtonExecutedAsync;
    }

    protected override void RegisterCommonChannelHandlers()
    {
        Receive<DiscordLogReceived>(HandleLogReceived);
        Receive<DiscordButtonDeferFailed>(HandleButtonDeferFailed);
    }

    protected override void RegisterNotReadyIngressHandlers() =>
        Receive<IGatewayIngressMessage>(DropIngress);

    protected override void RegisterDisconnectedChannelHandlers()
    {
        Receive<DiscordConnected>(_ => RequestCleanReconnect(
            "Discord gateway reconnected outside a clean startup cycle; forcing a clean reconnect."));
        Receive<DiscordReady>(_ => RequestCleanReconnect(
            "Discord gateway received READY outside a clean startup cycle; forcing a clean reconnect."));
        Receive<DiscordDisconnected>(HandleDisconnectedWhileNotReady);
    }

    protected override void RegisterConnectingChannelHandlers()
    {
        Receive<DiscordConnected>(_ => HealthDetail = "Discord gateway connected; waiting for READY.");
        Receive<DiscordReady>(_ => HandleReadyWhileConnecting());
        Receive<DiscordDisconnected>(HandleDisconnectedWhileConnecting);
    }

    protected override void RegisterReadyChannelHandlers()
    {
        Receive<DiscordConnected>(_ => RequestCleanReconnect(
            "Discord gateway reconnected outside a clean startup cycle; forcing a clean reconnect."));
        Receive<DiscordReady>(_ => HandleReadyRefresh());
        Receive<DiscordDisconnected>(HandleDisconnectedWhileReady);
        Receive<DiscordMessageReceived>(HandleMessageReceived);
        Receive<DiscordButtonExecuted>(HandleButtonExecuted);
        Receive<DiscordButtonDeferred>(HandleButtonDeferred);
    }

    protected override void RegisterDisconnectingChannelHandlers()
    {
        Receive<DiscordConnected>(_ => { });
        Receive<DiscordReady>(_ => { });
        Receive<DiscordDisconnected>(_ => { });
    }

    private Task OnDiscordLogAsync(LogMessage logMessage)
    {
        SelfRef.Tell(new DiscordLogReceived(logMessage));
        return Task.CompletedTask;
    }

    private Task OnConnectedAsync()
    {
        SelfRef.Tell(DiscordConnected.Instance);
        return Task.CompletedTask;
    }

    private Task OnReadyAsync()
    {
        SelfRef.Tell(DiscordReady.Instance);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception exception)
    {
        SelfRef.Tell(new DiscordDisconnected(exception));
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        SelfRef.Tell(new DiscordMessageReceived(message));
        return Task.CompletedTask;
    }

    private Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        SelfRef.Tell(new DiscordButtonExecuted(component));
        return Task.CompletedTask;
    }

    protected override async Task<object?> StartTransportAsync(Connect command)
    {
        await _client.LoginAsync(command.BotToken);
        await _client.StartAsync();
        return null;
    }

    protected override async Task StopTransportAsync()
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    protected override ChannelConnectException ClassifyStartFailure(Exception exception) =>
        DiscordConnectFailureClassifier.Classify(exception);

    protected override void OnTransportStartSucceeded(object? startResult)
    {
        HealthDetail = _client.ConnectionState == ConnectionState.Connected
            ? "Discord gateway connected; waiting for READY."
            : "Discord gateway started; waiting for READY.";
    }

    protected override void ResetIdentityState()
    {
        _botUserId = null;
        _botMentionTag = null;
        _fatalCloseHandled = false;
    }

    protected override DiscordGatewaySnapshot CreateSnapshot(bool isConnected, bool isReady, string? healthDetail) =>
        new(
            IsConnected: isConnected,
            IsReady: isReady,
            HealthDetail: healthDetail,
            BotUserId: _botUserId);

    protected override Task PublishCleanReconnectRequiredAsync(string reason) =>
        _eventSink.PublishCleanReconnectRequiredAsync(reason);

    protected override Task PublishConnectionRestoredAsync(DiscordGatewaySnapshot snapshot) =>
        _eventSink.PublishConnectionRestoredAsync(snapshot);

    private void HandleReadyWhileConnecting()
    {
        if (!TryRefreshBotIdentity("READY"))
        {
            // Canonical transient-fail path for both operator-driven and
            // auto-retry connects: fail any pending ask, publish
            // CleanReconnectRequired, stop the transport, and land in
            // Disconnected with the auto-retry scheduled.
            RequestCleanReconnect(
                "Discord gateway reached READY, but Discord.Net did not expose the current bot identity.");
            return;
        }

        CompleteConnectToReady();
    }

    private void HandleReadyRefresh()
    {
        if (TryRefreshBotIdentity("READY"))
        {
            HealthDetail = null;
            return;
        }

        RequestCleanReconnect(
            "Discord gateway reached READY, but Discord.Net did not expose the current bot identity.");
    }

    private void HandleDisconnectedWhileReady(DiscordDisconnected disconnected)
    {
        var classified = DiscordConnectFailureClassifier.Classify(disconnected.Exception);
        if (classified.IsFatal)
        {
            HandleFatalClose(classified);
            return;
        }

        WaitForReadyAfterTransportDrop();
    }

    private void HandleDisconnectedWhileConnecting(DiscordDisconnected disconnected)
    {
        var classified = DiscordConnectFailureClassifier.Classify(disconnected.Exception);
        if (classified.IsFatal)
        {
            HandleFatalClose(classified);
            return;
        }

        HealthDetail = DisconnectedDetail;
    }

    private void HandleDisconnectedWhileNotReady(DiscordDisconnected disconnected)
    {
        var classified = DiscordConnectFailureClassifier.Classify(disconnected.Exception);
        if (classified.IsFatal)
        {
            HandleFatalClose(classified);
            return;
        }

        HealthDetail = DisconnectedDetail;
    }

    /// <summary>
    /// Discord.Net reconnects on its own after a transport drop, so re-enter
    /// Connecting (with a fresh attempt stamp and ready timeout) and wait for
    /// its READY instead of tearing the client down.
    /// </summary>
    private void WaitForReadyAfterTransportDrop()
    {
        HealthDetail = DisconnectedDetail;
        WaitForReadySignal();
    }

    private void HandleFatalClose(ChannelConnectException classified)
    {
        HealthDetail = classified.Message;
        CancelReadySignalTimer();
        CancelAutoReconnect();
        FailPendingConnect(classified);
        BecomeDisconnectedBehavior();

        if (_fatalCloseHandled)
            return;

        _fatalCloseHandled = true;
        Logger.LogError(classified, "Gateway closed fatally: {Reason}", classified.Message);
        BeginStopAfterFatalClose();
    }

    private void BeginStopAfterFatalClose()
    {
        var self = Self;
        Task stopTask;
        try
        {
            stopTask = StopTransportAsync();
        }
        catch (Exception ex)
        {
            self.Tell(new DispatchFailed("Discord fatal-close stop", ex));
            return;
        }

        stopTask.ContinueWith(
            task =>
            {
                if (!task.IsCompletedSuccessfully)
                    self.Tell(new DispatchFailed("Discord fatal-close stop", UnwrapTaskException(task)));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleLogReceived(DiscordLogReceived received)
    {
        var logMessage = received.LogMessage;
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

        Logger.Log(level, logMessage.Exception, "[Discord.Net] {Source}: {Message}",
            logMessage.Source, logMessage.Message);

        if (string.Equals(logMessage.Source, "Gateway", StringComparison.Ordinal)
            && logMessage.Message?.Contains("Resumed previous session", StringComparison.OrdinalIgnoreCase) == true)
        {
            RequestCleanReconnect(
                "Discord.Net resumed a previous gateway session; forcing a clean reconnect to avoid stale resumed state.");
        }
    }

    private void HandleMessageReceived(DiscordMessageReceived received)
    {
        var message = received.Message;
        if (message is not SocketUserMessage userMessage)
            return;

        if (!IsReadyCore())
        {
            DropMessageReceived(received);
            return;
        }

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
            ReceivedAt: TimeProvider.GetUtcNow(),
            Attachments: attachments,
            IsInThread: isThread);

        Dispatch("Discord message " + message.Id, () => _eventSink.PublishMessageAsync(gatewayMessage));
    }

    private void HandleButtonExecuted(DiscordButtonExecuted executed)
    {
        if (!IsReadyCore())
        {
            DropButtonExecuted(executed);
            return;
        }

        BeginButtonDefer(executed.Component);
    }

    private void BeginButtonDefer(SocketMessageComponent component)
    {
        Task deferTask;
        try
        {
            deferTask = component.DeferAsync();
        }
        catch (Exception ex)
        {
            Self.Tell(new DiscordButtonDeferFailed(component.Id, ex));
            return;
        }

        if (deferTask.IsCompletedSuccessfully)
        {
            Self.Tell(new DiscordButtonDeferred(component));
            return;
        }

        var self = Self;
        deferTask.ContinueWith(
            task => self.Tell(task.IsCompletedSuccessfully
                ? new DiscordButtonDeferred(component)
                : new DiscordButtonDeferFailed(component.Id, UnwrapTaskException(task))),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleButtonDeferred(DiscordButtonDeferred deferred)
    {
        var component = deferred.Component;
        if (!IsReadyCore())
        {
            DropDeferredButton(deferred);
            return;
        }

        if (!ApprovalButtonValueCodec.TryDecode(
                component.Data.CustomId,
                out var callId,
                out var selectedKey,
                out var requesterSenderId))
        {
            Logger.LogWarning("Failed to parse button custom ID: {CustomId}", component.Data.CustomId);
            return;
        }

        var (channelId, replyChannelId, threadOrMessageId) = ResolveChannelContext(
            component.Channel, component.Message.Id);

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
            ReceivedAt: TimeProvider.GetUtcNow(),
            PromptMessageId: promptMessageId,
            ReplyChannelId: new DiscordReplyChannelId(replyChannelId));

        Dispatch("Discord button interaction " + component.Id, () => _eventSink.PublishInteractionAsync(interaction));
    }

    private void DropMessageReceived(DiscordMessageReceived received)
    {
        Logger.LogWarning(
            "Dropping Discord message {MessageId} while gateway is not ready: {Reason}",
            received.Message.Id,
            CurrentSnapshot().HealthDetail);
    }

    private void DropButtonExecuted(DiscordButtonExecuted executed)
    {
        Logger.LogWarning(
            "Dropping Discord interaction {InteractionId} while gateway is not ready: {Reason}",
            executed.Component.Id,
            CurrentSnapshot().HealthDetail);
    }

    private void DropDeferredButton(DiscordButtonDeferred deferred)
    {
        Logger.LogWarning(
            "Dropping deferred Discord interaction {InteractionId} while gateway is not ready: {Reason}",
            deferred.Component.Id,
            CurrentSnapshot().HealthDetail);
    }

    private void DropIngress(IGatewayIngressMessage message)
    {
        switch (message)
        {
            case DiscordMessageReceived received:
                DropMessageReceived(received);
                break;
            case DiscordButtonExecuted executed:
                DropButtonExecuted(executed);
                break;
            case DiscordButtonDeferred deferred:
                DropDeferredButton(deferred);
                break;
            default:
                Logger.LogWarning(
                    "Dropping Discord ingress message {MessageType} while gateway is not ready: {Reason}",
                    message.GetType().Name,
                    CurrentSnapshot().HealthDetail);
                break;
        }
    }

    private void HandleButtonDeferFailed(DiscordButtonDeferFailed failed) =>
        Logger.LogWarning(failed.Exception, "Failed to defer Discord button interaction {InteractionId}", failed.InteractionId);

    private bool TryRefreshBotIdentity(string source)
    {
        if (_client.CurrentUserId is not { } currentUserId)
        {
            HealthDetail = "Discord gateway is connected but the current bot identity is unavailable.";
            return false;
        }

        var botUserId = new DiscordUserId(currentUserId.ToString());
        var previousBotUserId = _botUserId;
        _botUserId = botUserId;
        _botMentionTag = $"<@{currentUserId}>";

        if (previousBotUserId == botUserId)
        {
            Logger.LogDebug("Bot identity refreshed from {Source}: {BotUserId}", source, currentUserId);
        }
        else if (string.Equals(source, "READY", StringComparison.Ordinal))
        {
            Logger.LogInformation("Bot identity resolved: {BotUserId}", currentUserId);
        }
        else
        {
            Logger.LogInformation("Bot identity resolved from {Source}: {BotUserId}", source, currentUserId);
        }

        return true;
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

    internal sealed record Connect(string BotToken);

    private sealed record DiscordConnected : IGatewayInternalMessage
    {
        public static readonly DiscordConnected Instance = new();
    }

    private sealed record DiscordReady : IGatewayInternalMessage
    {
        public static readonly DiscordReady Instance = new();
    }

    private sealed record DiscordDisconnected(Exception Exception) : IGatewayInternalMessage;

    private sealed record DiscordLogReceived(LogMessage LogMessage) : IGatewayInternalMessage;

    private sealed record DiscordMessageReceived(SocketMessage Message) : IGatewayIngressMessage;

    private sealed record DiscordButtonExecuted(SocketMessageComponent Component) : IGatewayIngressMessage;

    private sealed record DiscordButtonDeferred(SocketMessageComponent Component) : IGatewayIngressMessage;

    private sealed record DiscordButtonDeferFailed(ulong InteractionId, Exception Exception) : IGatewayInternalMessage;
}
