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

internal sealed class DiscordNetGatewayLifecycleActor : ReceiveActor, IWithTimers
{
    private const string DisconnectedDetail = "Discord gateway disconnected.";
    private const string ReadyTimeoutTimerKey = "discord-ready-timeout";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly IDiscordGatewayTransport _client;
    private readonly TimeProvider _timeProvider;
    private readonly IDiscordGatewayEventSink _eventSink;
    private readonly ILogger _logger;

    private bool _isReadyBehavior;
    private long _connectAttempt;
    private IActorRef _self = ActorRefs.Nobody;
    private IActorRef? _pendingConnectReplyTo;
    private DiscordUserId? _botUserId;
    private string? _botMentionTag;
    private string? _healthDetail = DisconnectedDetail;
    private bool _cleanReconnectEmitted;
    private bool _fatalCloseHandled;

    public ITimerScheduler Timers { get; set; } = null!;

    public DiscordNetGatewayLifecycleActor(
        IDiscordGatewayTransport client,
        TimeProvider timeProvider,
        IDiscordGatewayEventSink eventSink,
        ILogger logger)
    {
        _client = client;
        _timeProvider = timeProvider;
        _eventSink = eventSink;
        _logger = logger;

        Become(Disconnected);
    }

    public static Props CreateProps(
        IDiscordGatewayTransport client,
        TimeProvider timeProvider,
        IDiscordGatewayEventSink eventSink,
        ILogger logger) =>
        Props.Create(() => new DiscordNetGatewayLifecycleActor(client, timeProvider, eventSink, logger));

    protected override void PreStart()
    {
        _self = Self;
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

    private void Disconnected()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<Connect>(connect => StartConnecting(connect.BotToken, Sender));
        Receive<Disconnect>(_ => StartDisconnecting(Sender));
        Receive<DiscordConnected>(_ => RequestCleanReconnect(
            "Discord gateway reconnected outside a clean startup cycle; forcing a clean reconnect."));
        Receive<DiscordReady>(_ => RequestCleanReconnect(
            "Discord gateway received READY outside a clean startup cycle; forcing a clean reconnect."));
        Receive<DiscordDisconnected>(HandleDisconnectedWhileNotReady);
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(Disconnected));
    }

    private void Connecting()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<Connect>(_ => Sender.Tell(new Status.Failure(new InvalidOperationException(
            "Discord gateway connect is already in progress."))));
        Receive<Disconnect>(_ => StartDisconnecting(Sender));
        Receive<DiscordStartSucceeded>(HandleStartSucceeded);
        Receive<DiscordStartFailed>(HandleStartFailed);
        Receive<ReadyTimedOut>(HandleReadyTimedOut);
        Receive<DiscordConnected>(_ => _healthDetail = "Discord gateway connected; waiting for READY.");
        Receive<DiscordReady>(_ => HandleReadyWhileConnecting());
        Receive<DiscordDisconnected>(HandleDisconnectedWhileConnecting);
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(Connecting));
    }

    private void Ready()
    {
        _isReadyBehavior = true;
        ReceiveCommon();
        Receive<Connect>(_ => Sender.Tell(CurrentSnapshot()));
        Receive<Disconnect>(_ => StartDisconnecting(Sender));
        Receive<DiscordConnected>(_ => RequestCleanReconnect(
            "Discord gateway reconnected outside a clean startup cycle; forcing a clean reconnect."));
        Receive<DiscordReady>(_ => HandleReadyRefresh());
        Receive<DiscordDisconnected>(HandleDisconnectedWhileReady);
        Receive<DiscordMessageReceived>(HandleMessageReceived);
        Receive<DiscordButtonExecuted>(HandleButtonExecuted);
        Receive<DiscordButtonDeferred>(HandleButtonDeferred);
        ReceiveUnexpected(nameof(Ready));
    }

    private void CleanReconnectRequired()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<Connect>(_ => Sender.Tell(new Status.Failure(new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            "Discord gateway requires a clean disconnect before reconnecting."))));
        Receive<Disconnect>(_ => StartDisconnecting(Sender));
        Receive<DiscordConnected>(_ => { });
        Receive<DiscordReady>(_ => { });
        Receive<DiscordDisconnected>(HandleDisconnectedWhileNotReady);
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(CleanReconnectRequired));
    }

    private void Disconnecting()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<Connect>(_ => Sender.Tell(new Status.Failure(new InvalidOperationException(
            "Discord gateway disconnect is already in progress."))));
        Receive<Disconnect>(_ => Sender.Tell(new Status.Failure(new InvalidOperationException(
            "Discord gateway disconnect is already in progress."))));
        Receive<DiscordStopSucceeded>(HandleStopSucceeded);
        Receive<DiscordStopFailed>(HandleStopFailed);
        Receive<DiscordConnected>(_ => { });
        Receive<DiscordReady>(_ => { });
        Receive<DiscordDisconnected>(_ => { });
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(Disconnecting));
    }

    private void ReceiveCommon()
    {
        Receive<GetSnapshot>(_ => Sender.Tell(CurrentSnapshot()));
        Receive<DiscordLogReceived>(HandleLogReceived);
        Receive<DiscordButtonDeferFailed>(HandleButtonDeferFailed);
        Receive<DispatchFailed>(HandleDispatchFailed);
    }

    private void ReceiveNotReadyIngress()
    {
        Receive<IDiscordGatewayIngressMessage>(DropIngress);
    }

    private void ReceiveUnexpected(string behaviorName) =>
        ReceiveAny(message => HandleWrongBehaviorMessage(message, behaviorName));

    private Task OnDiscordLogAsync(LogMessage logMessage)
    {
        _self.Tell(new DiscordLogReceived(logMessage));
        return Task.CompletedTask;
    }

    private Task OnConnectedAsync()
    {
        _self.Tell(DiscordConnected.Instance);
        return Task.CompletedTask;
    }

    private Task OnReadyAsync()
    {
        _self.Tell(DiscordReady.Instance);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception exception)
    {
        _self.Tell(new DiscordDisconnected(exception));
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        _self.Tell(new DiscordMessageReceived(message));
        return Task.CompletedTask;
    }

    private Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        _self.Tell(new DiscordButtonExecuted(component));
        return Task.CompletedTask;
    }

    private void StartConnecting(string botToken, IActorRef replyTo)
    {
        _healthDetail = "Discord gateway connecting.";
        _botUserId = null;
        _botMentionTag = null;
        _cleanReconnectEmitted = false;
        _fatalCloseHandled = false;
        _pendingConnectReplyTo = replyTo;

        var attempt = ++_connectAttempt;
        Timers.StartSingleTimer(ReadyTimeoutTimerKey, new ReadyTimedOut(attempt), ReadyTimeout);
        Become(Connecting);
        BeginStart(botToken, attempt);
    }

    private void WaitForReadyAfterTransportDrop()
    {
        _healthDetail = DisconnectedDetail;
        var attempt = ++_connectAttempt;
        Timers.StartSingleTimer(ReadyTimeoutTimerKey, new ReadyTimedOut(attempt), ReadyTimeout);
        Become(Connecting);
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
        await _client.LoginAsync(botToken);
        await _client.StartAsync();
    }

    private void HandleStartSucceeded(DiscordStartSucceeded started)
    {
        if (started.Attempt != _connectAttempt)
            return;

        _healthDetail = _client.ConnectionState == ConnectionState.Connected
            ? "Discord gateway connected; waiting for READY."
            : "Discord gateway started; waiting for READY.";
    }

    private void HandleStartFailed(DiscordStartFailed failed)
    {
        if (failed.Attempt != _connectAttempt)
            return;

        _healthDetail = failed.Exception.Message;
        Timers.Cancel(ReadyTimeoutTimerKey);
        FailPendingConnect(failed.Exception);
        Become(Disconnected);
    }

    private void HandleReadyTimedOut(ReadyTimedOut timeout)
    {
        if (timeout.Attempt != _connectAttempt)
            return;

        var failure = new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            "Discord gateway did not reach READY within 30 seconds.");

        if (_pendingConnectReplyTo is not null)
        {
            _healthDetail = failure.Message;
            FailPendingConnect(failure);
            Become(CleanReconnectRequired);
            return;
        }

        RequestCleanReconnect(failure.Message);
    }

    private void StartDisconnecting(IActorRef replyTo)
    {
        ++_connectAttempt;
        _healthDetail = "Discord gateway disconnecting.";
        _cleanReconnectEmitted = false;
        Timers.Cancel(ReadyTimeoutTimerKey);
        FailPendingConnect(new OperationCanceledException("Discord gateway disconnect requested."));
        Become(Disconnecting);
        BeginStop(replyTo);
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
        _healthDetail = DisconnectedDetail;
        _botUserId = null;
        _botMentionTag = null;
        _fatalCloseHandled = false;
        Become(Disconnected);
        stopped.ReplyTo.Tell(CurrentSnapshot());
    }

    private void HandleStopFailed(DiscordStopFailed failed)
    {
        _healthDetail = failed.Exception.Message;
        Become(Disconnected);
        failed.ReplyTo.Tell(new Status.Failure(failed.Exception));
    }

    private void HandleReadyWhileConnecting()
    {
        if (!TryRefreshBotIdentity("READY"))
        {
            var failure = new ChannelConnectException(
                ChannelConnectFailureKind.Transient,
                "Discord gateway reached READY, but Discord.Net did not expose the current bot identity.");

            if (_pendingConnectReplyTo is not null)
            {
                _healthDetail = failure.Message;
                Timers.Cancel(ReadyTimeoutTimerKey);
                FailPendingConnect(failure);
                Become(CleanReconnectRequired);
            }
            else
            {
                RequestCleanReconnect(failure.Message);
            }

            return;
        }

        TransitionToReady();
        CompletePendingConnect(CurrentSnapshot());
    }

    private void HandleReadyRefresh()
    {
        if (TryRefreshBotIdentity("READY"))
        {
            _healthDetail = null;
            return;
        }

        RequestCleanReconnect(
            "Discord gateway reached READY, but Discord.Net did not expose the current bot identity.");
    }

    private void TransitionToReady()
    {
        _healthDetail = null;
        _cleanReconnectEmitted = false;
        _fatalCloseHandled = false;
        Timers.Cancel(ReadyTimeoutTimerKey);
        Become(Ready);
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

        _healthDetail = DisconnectedDetail;
    }

    private void HandleDisconnectedWhileNotReady(DiscordDisconnected disconnected)
    {
        var classified = DiscordConnectFailureClassifier.Classify(disconnected.Exception);
        if (classified.IsFatal)
        {
            HandleFatalClose(classified);
            return;
        }

        _healthDetail = DisconnectedDetail;
    }

    private void HandleFatalClose(ChannelConnectException classified)
    {
        _healthDetail = classified.Message;
        Timers.Cancel(ReadyTimeoutTimerKey);
        FailPendingConnect(classified);
        Become(Disconnected);

        if (_fatalCloseHandled)
            return;

        _fatalCloseHandled = true;
        _logger.LogError(classified, "Gateway closed fatally: {Reason}", classified.Message);
        BeginStopAfterFatalClose();
    }

    private void BeginStopAfterFatalClose()
    {
        var self = Self;
        Task stopTask;
        try
        {
            stopTask = StopDiscordClientAsync();
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
            ReceivedAt: _timeProvider.GetUtcNow(),
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

    private void DropMessageReceived(DiscordMessageReceived received)
    {
        _logger.LogWarning(
            "Dropping Discord message {MessageId} while gateway is not ready: {Reason}",
            received.Message.Id,
            CurrentSnapshot().HealthDetail);
    }

    private void DropButtonExecuted(DiscordButtonExecuted executed)
    {
        _logger.LogWarning(
            "Dropping Discord interaction {InteractionId} while gateway is not ready: {Reason}",
            executed.Component.Id,
            CurrentSnapshot().HealthDetail);
    }

    private void DropDeferredButton(DiscordButtonDeferred deferred)
    {
        _logger.LogWarning(
            "Dropping deferred Discord interaction {InteractionId} while gateway is not ready: {Reason}",
            deferred.Component.Id,
            CurrentSnapshot().HealthDetail);
    }

    private void DropIngress(IDiscordGatewayIngressMessage message)
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
                _logger.LogWarning(
                    "Dropping Discord ingress message {MessageType} while gateway is not ready: {Reason}",
                    message.GetType().Name,
                    CurrentSnapshot().HealthDetail);
                break;
        }
    }

    private void HandleButtonDeferFailed(DiscordButtonDeferFailed failed) =>
        _logger.LogWarning(failed.Exception, "Failed to defer Discord button interaction {InteractionId}", failed.InteractionId);

    private void HandleWrongBehaviorMessage(object message, string behaviorName)
    {
        switch (message)
        {
            case IDiscordGatewayConnectWork connectWork:
                IgnoreWrongBehaviorConnectWork(connectWork, behaviorName);
                break;
            case IDiscordGatewayStopWork stopWork:
                IgnoreWrongBehaviorStopWork(stopWork, behaviorName);
                break;
            case IDiscordGatewayInternalMessage:
                _logger.LogDebug(
                    "Ignoring Discord gateway internal message {MessageType} while in {State} state.",
                    message.GetType().Name,
                    behaviorName);
                break;
            default:
                _logger.LogWarning(
                    "Ignoring unexpected Discord gateway message {MessageType} while in {State} state.",
                    message.GetType().Name,
                    behaviorName);
                break;
        }
    }

    private void IgnoreWrongBehaviorConnectWork(
        IDiscordGatewayConnectWork connectWork,
        string behaviorName)
    {
        _logger.LogDebug(
            "Ignoring Discord gateway connect work {MessageType} for attempt {Attempt} while in {State} state; current attempt is {CurrentAttempt}.",
            connectWork.GetType().Name,
            connectWork.Attempt,
            behaviorName,
            _connectAttempt);

        if (_pendingConnectReplyTo is null)
            return;

        switch (connectWork)
        {
            case IDiscordGatewayConnectFailure failure:
                FailPendingConnect(failure.Exception);
                break;
            case ReadyTimedOut:
                FailPendingConnect(new ChannelConnectException(
                    ChannelConnectFailureKind.Transient,
                    "Discord gateway did not reach READY within 30 seconds."));
                break;
        }
    }

    private void IgnoreWrongBehaviorStopWork(IDiscordGatewayStopWork stopWork, string behaviorName)
    {
        _logger.LogDebug(
            "Ignoring Discord gateway stop work {MessageType} while in {State} state.",
            stopWork.GetType().Name,
            behaviorName);

        if (stopWork is IDiscordGatewayStopFailure failure)
        {
            stopWork.ReplyTo.Tell(new Status.Failure(failure.Exception));
            return;
        }

        stopWork.ReplyTo.Tell(CurrentSnapshot());
    }

    private void RequestCleanReconnect(string reason)
    {
        _healthDetail = reason;
        Timers.Cancel(ReadyTimeoutTimerKey);

        if (_pendingConnectReplyTo is not null)
        {
            FailPendingConnect(new ChannelConnectException(ChannelConnectFailureKind.Transient, reason));
            Become(CleanReconnectRequired);
            return;
        }

        Become(CleanReconnectRequired);

        if (_cleanReconnectEmitted)
            return;

        _cleanReconnectEmitted = true;
        _logger.LogWarning("Gateway requested clean reconnect: {Reason}", reason);
        Dispatch("Discord clean reconnect", () => _eventSink.PublishCleanReconnectRequiredAsync(reason));
    }

    private bool TryRefreshBotIdentity(string source)
    {
        if (_client.CurrentUserId is not { } currentUserId)
        {
            _healthDetail = "Discord gateway is connected but the current bot identity is unavailable.";
            return false;
        }

        var botUserId = new DiscordUserId(currentUserId.ToString());
        var previousBotUserId = _botUserId;
        _botUserId = botUserId;
        _botMentionTag = $"<@{currentUserId}>";

        if (previousBotUserId == botUserId)
        {
            _logger.LogDebug("Bot identity refreshed from {Source}: {BotUserId}", source, currentUserId);
        }
        else if (string.Equals(source, "READY", StringComparison.Ordinal))
        {
            _logger.LogInformation("Bot identity resolved: {BotUserId}", currentUserId);
        }
        else
        {
            _logger.LogInformation("Bot identity resolved from {Source}: {BotUserId}", source, currentUserId);
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
        _isReadyBehavior
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

    private interface IDiscordGatewayInternalMessage;

    private interface IDiscordGatewayIngressMessage : IDiscordGatewayInternalMessage;

    private interface IDiscordGatewayConnectWork : IDiscordGatewayInternalMessage
    {
        long Attempt { get; }
    }

    private interface IDiscordGatewayConnectFailure : IDiscordGatewayConnectWork
    {
        Exception Exception { get; }
    }

    private interface IDiscordGatewayStopWork : IDiscordGatewayInternalMessage
    {
        IActorRef ReplyTo { get; }
    }

    private interface IDiscordGatewayStopFailure : IDiscordGatewayStopWork
    {
        Exception Exception { get; }
    }

    private sealed record DiscordStartSucceeded(long Attempt) : IDiscordGatewayConnectWork;

    private sealed record DiscordStartFailed(long Attempt, Exception Exception) : IDiscordGatewayConnectFailure;

    private sealed record ReadyTimedOut(long Attempt) : IDiscordGatewayConnectWork;

    private sealed record DiscordStopSucceeded(IActorRef ReplyTo) : IDiscordGatewayStopWork;

    private sealed record DiscordStopFailed(IActorRef ReplyTo, Exception Exception) : IDiscordGatewayStopFailure;

    private sealed record DiscordConnected : IDiscordGatewayInternalMessage
    {
        public static readonly DiscordConnected Instance = new();
    }

    private sealed record DiscordReady : IDiscordGatewayInternalMessage
    {
        public static readonly DiscordReady Instance = new();
    }

    private sealed record DiscordDisconnected(Exception Exception) : IDiscordGatewayInternalMessage;

    private sealed record DiscordLogReceived(LogMessage LogMessage) : IDiscordGatewayInternalMessage;

    private sealed record DiscordMessageReceived(SocketMessage Message) : IDiscordGatewayIngressMessage;

    private sealed record DiscordButtonExecuted(SocketMessageComponent Component) : IDiscordGatewayIngressMessage;

    private sealed record DiscordButtonDeferred(SocketMessageComponent Component) : IDiscordGatewayIngressMessage;

    private sealed record DiscordButtonDeferFailed(ulong InteractionId, Exception Exception) : IDiscordGatewayInternalMessage;

    private sealed record DispatchFailed(string Operation, Exception Exception) : IDiscordGatewayInternalMessage;
}
