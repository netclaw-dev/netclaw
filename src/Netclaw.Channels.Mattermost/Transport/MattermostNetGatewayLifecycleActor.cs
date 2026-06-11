// -----------------------------------------------------------------------
// <copyright file="MattermostNetGatewayLifecycleActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging;

namespace Netclaw.Channels.Mattermost.Transport;

internal interface IMattermostGatewayEventSink
{
    Task PublishMessageAsync(MattermostGatewayMessage message);

    Task PublishCleanReconnectRequiredAsync(string reason);

    Task PublishConnectionRestoredAsync(MattermostGatewaySnapshot snapshot);
}

internal interface IMattermostGatewayTransport
{
    event Func<MattermostGatewayMessage, Task> MessageReceived;

    event Func<Task> Connected;

    event Func<MattermostGatewayDisconnect, Task> Disconnected;

    event Func<string, Task> LogReceived;

    bool IsConnected { get; }

    Task<MattermostBotIdentity> StartAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default);

    Task StopAsync();
}

internal sealed record MattermostBotIdentity(string UserId, string Username);

internal sealed record MattermostGatewayDisconnect(string? Reason, Exception? Exception = null);

/// <summary>
/// Mattermost gateway lifecycle: the shared state machine lives in
/// <see cref="ChannelLifecycleActor{TSnapshot,TConnectCommand}"/>. Mattermost's
/// StartAsync returning successfully IS the ready signal — the bot identity is
/// validated from the start result and the actor transitions straight to Ready.
/// </summary>
internal sealed class MattermostNetGatewayLifecycleActor :
    ChannelLifecycleActor<MattermostGatewaySnapshot, MattermostNetGatewayLifecycleActor.Connect>
{
    private readonly IMattermostGatewayTransport _transport;
    private readonly IMattermostGatewayEventSink _eventSink;

    private MattermostUserId? _botUserId;
    private string? _botUsername;

    public MattermostNetGatewayLifecycleActor(
        IMattermostGatewayTransport transport,
        TimeProvider timeProvider,
        IMattermostGatewayEventSink eventSink,
        ILogger logger)
        : base("Mattermost", timeProvider, logger)
    {
        _transport = transport;
        _eventSink = eventSink;
    }

    public static Props CreateProps(
        IMattermostGatewayTransport transport,
        TimeProvider timeProvider,
        IMattermostGatewayEventSink eventSink,
        ILogger logger) =>
        Props.Create(() => new MattermostNetGatewayLifecycleActor(transport, timeProvider, eventSink, logger));

    protected override bool IsTransportConnected => _transport.IsConnected;

    protected override bool HasBotIdentity =>
        _botUserId is not null && !string.IsNullOrWhiteSpace(_botUsername);

    protected override void SubscribeTransportEvents()
    {
        _transport.MessageReceived += OnMessageReceivedAsync;
        _transport.Connected += OnConnectedAsync;
        _transport.Disconnected += OnDisconnectedAsync;
        _transport.LogReceived += OnLogReceivedAsync;
    }

    protected override void UnsubscribeTransportEvents()
    {
        _transport.MessageReceived -= OnMessageReceivedAsync;
        _transport.Connected -= OnConnectedAsync;
        _transport.Disconnected -= OnDisconnectedAsync;
        _transport.LogReceived -= OnLogReceivedAsync;
    }

    protected override void RegisterCommonChannelHandlers() =>
        Receive<MattermostLogReceived>(HandleLogReceived);

    protected override void RegisterNotReadyIngressHandlers() =>
        Receive<MattermostMessageReceived>(DropMessageReceived);

    protected override void RegisterDisconnectedChannelHandlers()
    {
        Receive<MattermostConnected>(_ => RequestCleanReconnect(
            "Mattermost gateway reconnected outside a clean startup cycle; forcing a clean reconnect."));
        Receive<MattermostDisconnected>(HandleDisconnectedWhileNotReady);
    }

    protected override void RegisterConnectingChannelHandlers()
    {
        Receive<MattermostConnected>(_ => HealthDetail = "Mattermost gateway connected; completing startup.");
        Receive<MattermostDisconnected>(HandleDisconnectedWhileConnecting);
    }

    protected override void RegisterReadyChannelHandlers()
    {
        Receive<MattermostConnected>(_ => HealthDetail = null);
        Receive<MattermostDisconnected>(HandleDisconnectedWhileReady);
        Receive<MattermostMessageReceived>(HandleMessageReceived);
    }

    protected override void RegisterDisconnectingChannelHandlers()
    {
        Receive<MattermostConnected>(_ => { });
        Receive<MattermostDisconnected>(_ => { });
    }

    private Task OnMessageReceivedAsync(MattermostGatewayMessage message)
    {
        SelfRef.Tell(new MattermostMessageReceived(message));
        return Task.CompletedTask;
    }

    private Task OnConnectedAsync()
    {
        SelfRef.Tell(MattermostConnected.Instance);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MattermostGatewayDisconnect disconnect)
    {
        SelfRef.Tell(new MattermostDisconnected(disconnect));
        return Task.CompletedTask;
    }

    private Task OnLogReceivedAsync(string message)
    {
        SelfRef.Tell(new MattermostLogReceived(message));
        return Task.CompletedTask;
    }

    protected override async Task<object?> StartTransportAsync(Connect command) =>
        await _transport.StartAsync(command.ServerUrl, command.BotToken, CancellationToken.None);

    protected override Task StopTransportAsync() => _transport.StopAsync();

    protected override ChannelConnectException ClassifyStartFailure(Exception exception) =>
        MattermostConnectFailureClassifier.Classify(exception);

    protected override void OnTransportStartSucceeded(object? startResult)
    {
        var identity = (MattermostBotIdentity)startResult!;

        if (string.IsNullOrWhiteSpace(identity.UserId)
            || string.IsNullOrWhiteSpace(identity.Username))
        {
            FailStartAndReturnToDisconnected(new ChannelConnectException(
                ChannelConnectFailureKind.Transient,
                "Mattermost gateway connected but the current bot identity is unavailable."));
            return;
        }

        if (!_transport.IsConnected)
        {
            FailStartAndReturnToDisconnected(new ChannelConnectException(
                ChannelConnectFailureKind.Transient,
                "Mattermost gateway started but the WebSocket is not connected."));
            return;
        }

        _botUserId = new MattermostUserId(identity.UserId);
        _botUsername = identity.Username;
        CompleteConnectToReady();
    }

    private void FailStartAndReturnToDisconnected(ChannelConnectException failure)
    {
        HealthDetail = failure.Message;
        FailPendingConnect(failure);
        ScheduleRetryIfEnabled();
        BecomeDisconnectedBehavior();
    }

    protected override void ResetIdentityState()
    {
        _botUserId = null;
        _botUsername = null;
    }

    protected override MattermostGatewaySnapshot CreateSnapshot(bool isConnected, bool isReady, string? healthDetail) =>
        new(
            IsConnected: isConnected,
            IsReady: isReady,
            HealthDetail: healthDetail,
            BotUserId: _botUserId,
            BotUsername: _botUsername);

    protected override Task PublishCleanReconnectRequiredAsync(string reason) =>
        _eventSink.PublishCleanReconnectRequiredAsync(reason);

    protected override Task PublishConnectionRestoredAsync(MattermostGatewaySnapshot snapshot) =>
        _eventSink.PublishConnectionRestoredAsync(snapshot);

    private void HandleDisconnectedWhileReady(MattermostDisconnected disconnected)
    {
        var detail = EndSentence(BuildDisconnectDetail(disconnected.Disconnect));
        RequestCleanReconnect(detail + " A clean reconnect is required.");
    }

    private void HandleDisconnectedWhileConnecting(MattermostDisconnected disconnected)
    {
        HealthDetail = BuildDisconnectDetail(disconnected.Disconnect);
    }

    private void HandleDisconnectedWhileNotReady(MattermostDisconnected disconnected)
    {
        HealthDetail = BuildDisconnectDetail(disconnected.Disconnect);
    }

    private void HandleMessageReceived(MattermostMessageReceived received)
    {
        if (!IsReadyCore())
        {
            DropMessageReceived(received);
            return;
        }

        Dispatch(
            "Mattermost message " + received.Message.EventId.Value,
            () => _eventSink.PublishMessageAsync(received.Message));
    }

    private void DropMessageReceived(MattermostMessageReceived received)
    {
        Logger.LogWarning(
            "Dropping Mattermost message {EventId} while gateway is not ready: {Reason}",
            received.Message.EventId.Value,
            CurrentSnapshot().HealthDetail);
    }

    private void HandleLogReceived(MattermostLogReceived received) =>
        Logger.LogDebug("[Mattermost.NET] {Message}", received.Message);

    private string BuildDisconnectDetail(MattermostGatewayDisconnect disconnect)
    {
        var reason = disconnect.Exception?.Message ?? disconnect.Reason;
        return string.IsNullOrWhiteSpace(reason)
            ? DisconnectedDetail
            : "Mattermost gateway disconnected: " + reason;
    }

    private static string EndSentence(string message) =>
        message.EndsWith(".", StringComparison.Ordinal) ? message : message + ".";

    internal sealed record Connect(string ServerUrl, string BotToken);

    private sealed record MattermostConnected : IGatewayInternalMessage
    {
        public static readonly MattermostConnected Instance = new();
    }

    private sealed record MattermostDisconnected(MattermostGatewayDisconnect Disconnect) : IGatewayInternalMessage;

    private sealed record MattermostLogReceived(string Message) : IGatewayInternalMessage;

    private sealed record MattermostMessageReceived(MattermostGatewayMessage Message) : IGatewayInternalMessage;
}
