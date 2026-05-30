// -----------------------------------------------------------------------
// <copyright file="DiscordNetGatewayClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Discord.Transport;

internal sealed class DiscordNetGatewayClient : IDiscordGatewayClient, IDiscordGatewayEventSink, IDisposable
{
    private static readonly TimeSpan ConnectAskTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan SnapshotAskTimeout = TimeSpan.FromSeconds(5);

    private readonly ActorSystem _actorSystem;
    private readonly IActorRef _lifecycleActor;

    public event Func<DiscordGatewayMessage, Task>? MessageReceived;
    public event Func<DiscordGatewayInteraction, Task>? InteractionReceived;
    public event Func<string, Task>? CleanReconnectRequired;

    internal enum DiscordChannelKind
    {
        GuildChannel,
        Thread,
        DirectMessage,
    }

    public DiscordNetGatewayClient(
        ActorSystem actorSystem,
        DiscordSocketClient client,
        TimeProvider timeProvider,
        ILogger<DiscordNetGatewayClient> logger)
    {
        _actorSystem = actorSystem;
        _lifecycleActor = actorSystem.ActorOf(
            DiscordNetGatewayLifecycleActor.CreateProps(client, timeProvider, this, logger),
            "discord-net-gateway-lifecycle");
    }

    public Task<DiscordGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        _lifecycleActor.Ask<DiscordGatewaySnapshot>(
            DiscordNetGatewayLifecycleActor.GetSnapshot.Instance,
            SnapshotAskTimeout,
            cancellationToken: cancellationToken);

    public Task<DiscordGatewaySnapshot> ConnectAsync(string botToken, CancellationToken cancellationToken = default) =>
        _lifecycleActor.Ask<DiscordGatewaySnapshot>(
            new DiscordNetGatewayLifecycleActor.Connect(botToken),
            ConnectAskTimeout,
            cancellationToken: cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleActor.Ask<DiscordGatewaySnapshot>(
            DiscordNetGatewayLifecycleActor.Disconnect.Instance,
            ConnectAskTimeout,
            cancellationToken: cancellationToken);
    }

    public void Dispose() => _actorSystem.Stop(_lifecycleActor);

    internal static (string ChannelId, string ReplyChannelId, string ThreadOrMessageId) ResolveChannelContext(
        ulong channelId, ulong messageId,
        DiscordChannelKind kind,
        ulong? parentChannelId) =>
        DiscordNetGatewayLifecycleActor.ResolveChannelContext(
            channelId,
            messageId,
            kind switch
            {
                DiscordChannelKind.Thread => DiscordNetGatewayLifecycleActor.DiscordChannelKind.Thread,
                DiscordChannelKind.DirectMessage => DiscordNetGatewayLifecycleActor.DiscordChannelKind.DirectMessage,
                _ => DiscordNetGatewayLifecycleActor.DiscordChannelKind.GuildChannel,
            },
            parentChannelId);

    Task IDiscordGatewayEventSink.PublishMessageAsync(DiscordGatewayMessage message) =>
        MessageReceived?.Invoke(message) ?? Task.CompletedTask;

    Task IDiscordGatewayEventSink.PublishInteractionAsync(DiscordGatewayInteraction interaction) =>
        InteractionReceived?.Invoke(interaction) ?? Task.CompletedTask;

    Task IDiscordGatewayEventSink.PublishCleanReconnectRequiredAsync(string reason) =>
        CleanReconnectRequired?.Invoke(reason) ?? Task.CompletedTask;
}

internal interface IDiscordGatewayEventSink
{
    Task PublishMessageAsync(DiscordGatewayMessage message);

    Task PublishInteractionAsync(DiscordGatewayInteraction interaction);

    Task PublishCleanReconnectRequiredAsync(string reason);
}

internal sealed class DiscordNetGatewayLifecycleActor : ReceiveActor, IWithTimers
{
    private const string DisconnectedDetail = "Discord gateway disconnected.";
    private const string ReadyTimeoutTimerKey = "discord-ready-timeout";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly DiscordSocketClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly IDiscordGatewayEventSink _eventSink;
    private readonly ILogger _logger;

    private GatewayLifecycleState _state = GatewayLifecycleState.Disconnected;
    private long _connectAttempt;
    private IActorRef? _pendingConnectReplyTo;
    private DiscordUserId? _botUserId;
    private string? _botMentionTag;
    private string? _healthDetail = DisconnectedDetail;
    private bool _cleanReconnectEmitted;

    public ITimerScheduler Timers { get; set; } = null!;

    private DiscordNetGatewayLifecycleActor(
        DiscordSocketClient client,
        TimeProvider timeProvider,
        IDiscordGatewayEventSink eventSink,
        ILogger logger)
    {
        _client = client;
        _timeProvider = timeProvider;
        _eventSink = eventSink;
        _logger = logger;

        Receive<GetSnapshot>(_ => Sender.Tell(CurrentSnapshot()));
        Receive<Connect>(HandleConnect);
        Receive<DiscordStartSucceeded>(HandleStartSucceeded);
        Receive<DiscordStartFailed>(HandleStartFailed);
        Receive<ReadyTimedOut>(HandleReadyTimedOut);
        Receive<Disconnect>(HandleDisconnect);
        Receive<DiscordStopSucceeded>(HandleStopSucceeded);
        Receive<DiscordStopFailed>(HandleStopFailed);
        Receive<DiscordConnected>(_ => HandleConnected());
        Receive<DiscordReady>(_ => HandleReady());
        Receive<DiscordDisconnected>(HandleDisconnected);
        Receive<DiscordLogReceived>(HandleLogReceived);
        Receive<DiscordMessageReceived>(HandleMessageReceived);
        Receive<DiscordButtonExecuted>(HandleButtonExecuted);
        Receive<DiscordButtonDeferred>(HandleButtonDeferred);
        Receive<DiscordButtonDeferFailed>(HandleButtonDeferFailed);
        Receive<DispatchFailed>(HandleDispatchFailed);
    }

    public static Props CreateProps(
        DiscordSocketClient client,
        TimeProvider timeProvider,
        IDiscordGatewayEventSink eventSink,
        ILogger logger) =>
        Props.Create(() => new DiscordNetGatewayLifecycleActor(client, timeProvider, eventSink, logger));

    protected override void PreStart()
    {
        _client.Log += OnDiscordLogAsync;
        _client.Connected += OnConnectedAsync;
        _client.Ready += OnReadyAsync;
        _client.Disconnected += OnDisconnectedAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.ButtonExecuted += OnButtonExecutedAsync;
        base.PreStart();
    }

    protected override void PostStop()
    {
        _client.Log -= OnDiscordLogAsync;
        _client.Connected -= OnConnectedAsync;
        _client.Ready -= OnReadyAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.ButtonExecuted -= OnButtonExecutedAsync;
        base.PostStop();
    }

    private Task OnDiscordLogAsync(LogMessage logMessage)
    {
        Self.Tell(new DiscordLogReceived(logMessage));
        return Task.CompletedTask;
    }

    private Task OnConnectedAsync()
    {
        Self.Tell(DiscordConnected.Instance);
        return Task.CompletedTask;
    }

    private Task OnReadyAsync()
    {
        Self.Tell(DiscordReady.Instance);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception exception)
    {
        Self.Tell(new DiscordDisconnected(exception));
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        Self.Tell(new DiscordMessageReceived(message));
        return Task.CompletedTask;
    }

    private Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        Self.Tell(new DiscordButtonExecuted(component));
        return Task.CompletedTask;
    }

    private void HandleConnect(Connect connect)
    {
        if (IsReadyCore())
        {
            Sender.Tell(CurrentSnapshot());
            return;
        }

        if (_pendingConnectReplyTo is not null)
        {
            Sender.Tell(new Status.Failure(new InvalidOperationException(
                "Discord gateway connect is already in progress.")));
            return;
        }

        _state = GatewayLifecycleState.Connecting;
        _healthDetail = "Discord gateway connecting.";
        _botUserId = null;
        _botMentionTag = null;
        _cleanReconnectEmitted = false;
        _pendingConnectReplyTo = Sender;

        var attempt = ++_connectAttempt;
        Timers.StartSingleTimer(ReadyTimeoutTimerKey, new ReadyTimedOut(attempt), ReadyTimeout);
        BeginStart(connect.BotToken, attempt);
    }

    private void BeginStart(string botToken, long attempt)
    {
        var self = Self;
        Task startTask;
        try
        {
            startTask = StartDiscordClientAsync(botToken);
        }
        catch (Exception ex)
        {
            self.Tell(new DiscordStartFailed(attempt, ex));
            return;
        }

        startTask.ContinueWith(
            task => self.Tell(task.IsCompletedSuccessfully
                ? new DiscordStartSucceeded(attempt)
                : new DiscordStartFailed(attempt, UnwrapTaskException(task))),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task StartDiscordClientAsync(string botToken)
    {
        await _client.LoginAsync(TokenType.Bot, botToken);
        await _client.StartAsync();
    }

    private void HandleStartSucceeded(DiscordStartSucceeded started)
    {
        if (started.Attempt != _connectAttempt || _state != GatewayLifecycleState.Connecting)
            return;

        _healthDetail = _client.ConnectionState == ConnectionState.Connected
            ? "Discord gateway connected; waiting for READY."
            : "Discord gateway started; waiting for READY.";
    }

    private void HandleStartFailed(DiscordStartFailed failed)
    {
        if (failed.Attempt != _connectAttempt || _state != GatewayLifecycleState.Connecting)
            return;

        _state = GatewayLifecycleState.Disconnected;
        _healthDetail = failed.Exception.Message;
        Timers.Cancel(ReadyTimeoutTimerKey);
        FailPendingConnect(failed.Exception);
    }

    private void HandleReadyTimedOut(ReadyTimedOut timeout)
    {
        if (timeout.Attempt != _connectAttempt || _state != GatewayLifecycleState.Connecting)
            return;

        var failure = new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            "Discord gateway did not reach READY within 30 seconds.");
        _state = GatewayLifecycleState.CleanReconnectRequired;
        _healthDetail = failure.Message;
        Timers.Cancel(ReadyTimeoutTimerKey);
        FailPendingConnect(failure);
    }

    private void HandleDisconnect(Disconnect _)
    {
        ++_connectAttempt;
        _state = GatewayLifecycleState.Disconnecting;
        _healthDetail = "Discord gateway disconnecting.";
        _cleanReconnectEmitted = false;
        Timers.Cancel(ReadyTimeoutTimerKey);
        FailPendingConnect(new OperationCanceledException("Discord gateway disconnect requested."));

        BeginStop(Sender);
    }

    private void BeginStop(IActorRef replyTo)
    {
        var self = Self;
        Task stopTask;
        try
        {
            stopTask = StopDiscordClientAsync();
        }
        catch (Exception ex)
        {
            self.Tell(new DiscordStopFailed(replyTo, ex));
            return;
        }

        stopTask.ContinueWith(
            task => self.Tell(task.IsCompletedSuccessfully
                ? new DiscordStopSucceeded(replyTo)
                : new DiscordStopFailed(replyTo, UnwrapTaskException(task))),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task StopDiscordClientAsync()
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    private void HandleStopSucceeded(DiscordStopSucceeded stopped)
    {
        _state = GatewayLifecycleState.Disconnected;
        _healthDetail = DisconnectedDetail;
        _botUserId = null;
        _botMentionTag = null;
        stopped.ReplyTo.Tell(CurrentSnapshot());
    }

    private void HandleStopFailed(DiscordStopFailed failed)
    {
        _state = GatewayLifecycleState.Disconnected;
        _healthDetail = failed.Exception.Message;
        failed.ReplyTo.Tell(new Status.Failure(failed.Exception));
    }

    private void HandleConnected()
    {
        if (_state == GatewayLifecycleState.Connecting)
        {
            _healthDetail = "Discord gateway connected; waiting for READY.";
            return;
        }

        if (_state == GatewayLifecycleState.Disconnecting)
            return;

        RequestCleanReconnect(
            "Discord gateway reconnected outside a clean startup cycle; forcing a clean reconnect.");
    }

    private void HandleReady()
    {
        if (!TryRefreshBotIdentity("READY"))
        {
            var failure = new ChannelConnectException(
                ChannelConnectFailureKind.Transient,
                "Discord gateway reached READY, but Discord.Net did not expose the current bot identity.");
            _healthDetail = failure.Message;

            if (_state == GatewayLifecycleState.Connecting)
            {
                _state = GatewayLifecycleState.CleanReconnectRequired;
                Timers.Cancel(ReadyTimeoutTimerKey);
                FailPendingConnect(failure);
            }
            else
            {
                RequestCleanReconnect(failure.Message);
            }

            return;
        }

        _state = GatewayLifecycleState.Ready;
        _healthDetail = null;
        _cleanReconnectEmitted = false;
        Timers.Cancel(ReadyTimeoutTimerKey);
        CompletePendingConnect(CurrentSnapshot());
    }

    private void HandleDisconnected(DiscordDisconnected disconnected)
    {
        var classified = DiscordConnectFailureClassifier.Classify(disconnected.Exception);

        if (!classified.IsFatal)
        {
            if (_state != GatewayLifecycleState.Connecting)
                _state = GatewayLifecycleState.Disconnected;

            _healthDetail = DisconnectedDetail;
            return;
        }

        _state = GatewayLifecycleState.Disconnected;
        _healthDetail = classified.Message;
        Timers.Cancel(ReadyTimeoutTimerKey);
        FailPendingConnect(classified);

        _logger.LogError(classified, "Discord gateway closed fatally: {Reason}", classified.Message);
        BeginStopAfterFatalClose();
    }

    private void BeginStopAfterFatalClose()
    {
        var self = Self;
        StopDiscordClientAsync().ContinueWith(
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

        _logger.Log(level, logMessage.Exception, "[Discord.Net] {Source}: {Message}",
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
            _logger.LogWarning(
                "Dropping Discord message {MessageId} while gateway is not ready: {Reason}",
                message.Id,
                CurrentSnapshot().HealthDetail);
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
            ReceivedAt: _timeProvider.GetUtcNow(),
            Attachments: attachments,
            IsInThread: isThread);

        Dispatch("Discord message " + message.Id, () => _eventSink.PublishMessageAsync(gatewayMessage));
    }

    private void HandleButtonExecuted(DiscordButtonExecuted executed)
    {
        var component = executed.Component;
        if (!IsReadyCore())
        {
            _logger.LogWarning(
                "Dropping Discord interaction {InteractionId} while gateway is not ready: {Reason}",
                component.Id,
                CurrentSnapshot().HealthDetail);
            return;
        }

        BeginButtonDefer(component);
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
            _logger.LogWarning(
                "Dropping deferred Discord interaction {InteractionId} while gateway is not ready: {Reason}",
                component.Id,
                CurrentSnapshot().HealthDetail);
            return;
        }

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
            ReplyChannelId: new DiscordReplyChannelId(replyChannelId));

        Dispatch("Discord button interaction " + component.Id, () => _eventSink.PublishInteractionAsync(interaction));
    }

    private void HandleButtonDeferFailed(DiscordButtonDeferFailed failed) =>
        _logger.LogWarning(failed.Exception, "Failed to defer Discord button interaction {InteractionId}", failed.InteractionId);

    private void RequestCleanReconnect(string reason)
    {
        _healthDetail = reason;

        if (_state == GatewayLifecycleState.Connecting)
        {
            _state = GatewayLifecycleState.CleanReconnectRequired;
            Timers.Cancel(ReadyTimeoutTimerKey);
            FailPendingConnect(new ChannelConnectException(ChannelConnectFailureKind.Transient, reason));
            return;
        }

        _state = GatewayLifecycleState.CleanReconnectRequired;

        if (_cleanReconnectEmitted)
            return;

        _cleanReconnectEmitted = true;
        _logger.LogWarning("Discord gateway requested clean reconnect: {Reason}", reason);
        Dispatch("Discord clean reconnect", () => _eventSink.PublishCleanReconnectRequiredAsync(reason));
    }

    private bool TryRefreshBotIdentity(string source)
    {
        if (_client.CurrentUser is not { } currentUser)
        {
            _healthDetail = "Discord gateway is connected but the current bot identity is unavailable.";
            return false;
        }

        var botUserId = new DiscordUserId(currentUser.Id.ToString());
        var previousBotUserId = _botUserId;
        _botUserId = botUserId;
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

    private DiscordGatewaySnapshot CurrentSnapshot()
    {
        var isReady = IsReadyCore();
        var healthDetail = isReady
            ? null
            : _healthDetail ?? (_client.ConnectionState == ConnectionState.Connected
                ? "Discord gateway connected but not ready."
                : DisconnectedDetail);

        return new DiscordGatewaySnapshot(
            IsConnected: _client.ConnectionState == ConnectionState.Connected,
            IsReady: isReady,
            HealthDetail: healthDetail,
            BotUserId: _botUserId);
    }

    private bool IsReadyCore() =>
        _state == GatewayLifecycleState.Ready
        && _botUserId is not null
        && _botMentionTag is not null
        && _client.ConnectionState == ConnectionState.Connected;

    private void CompletePendingConnect(DiscordGatewaySnapshot snapshot)
    {
        var replyTo = _pendingConnectReplyTo;
        _pendingConnectReplyTo = null;
        replyTo?.Tell(snapshot);
    }

    private void FailPendingConnect(Exception exception)
    {
        var replyTo = _pendingConnectReplyTo;
        _pendingConnectReplyTo = null;
        replyTo?.Tell(new Status.Failure(exception));
    }

    private void Dispatch(string operation, Func<Task> dispatch)
    {
        Task dispatchTask;
        try
        {
            dispatchTask = dispatch();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {Operation}", operation);
            return;
        }

        if (dispatchTask.IsCompletedSuccessfully)
            return;

        var self = Self;
        dispatchTask.ContinueWith(
            task =>
            {
                if (!task.IsCompletedSuccessfully)
                    self.Tell(new DispatchFailed(operation, UnwrapTaskException(task)));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleDispatchFailed(DispatchFailed failed) =>
        _logger.LogError(failed.Exception, "Error handling {Operation}", failed.Operation);

    private static Exception UnwrapTaskException(Task task)
    {
        if (task.IsCanceled)
            return new TaskCanceledException(task);

        return task.Exception?.GetBaseException()
               ?? new InvalidOperationException("Discord gateway operation failed without an exception.");
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

    internal sealed record GetSnapshot
    {
        public static readonly GetSnapshot Instance = new();
    }

    internal sealed record Disconnect
    {
        public static readonly Disconnect Instance = new();
    }

    private sealed record DiscordStartSucceeded(long Attempt);

    private sealed record DiscordStartFailed(long Attempt, Exception Exception);

    private sealed record ReadyTimedOut(long Attempt);

    private sealed record DiscordStopSucceeded(IActorRef ReplyTo);

    private sealed record DiscordStopFailed(IActorRef ReplyTo, Exception Exception);

    private sealed record DiscordConnected
    {
        public static readonly DiscordConnected Instance = new();
    }

    private sealed record DiscordReady
    {
        public static readonly DiscordReady Instance = new();
    }

    private sealed record DiscordDisconnected(Exception Exception);

    private sealed record DiscordLogReceived(LogMessage LogMessage);

    private sealed record DiscordMessageReceived(SocketMessage Message);

    private sealed record DiscordButtonExecuted(SocketMessageComponent Component);

    private sealed record DiscordButtonDeferred(SocketMessageComponent Component);

    private sealed record DiscordButtonDeferFailed(ulong InteractionId, Exception Exception);

    private sealed record DispatchFailed(string Operation, Exception Exception);

    private enum GatewayLifecycleState
    {
        Disconnected,
        Connecting,
        Ready,
        CleanReconnectRequired,
        Disconnecting,
    }
}
