// -----------------------------------------------------------------------
// <copyright file="MattermostNetGatewayClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Pattern;
using Mattermost;
using Mattermost.Events;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;

namespace Netclaw.Channels.Mattermost.Transport;

internal sealed class MattermostNetGatewayClient : IMattermostGatewayClient, IMattermostGatewayEventSink, IDisposable
{
    private static readonly TimeSpan ConnectAskTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan SnapshotAskTimeout = TimeSpan.FromSeconds(5);

    private readonly ActorSystem _actorSystem;
    private readonly IActorRef _lifecycleActor;
    private volatile MattermostGatewaySnapshot _latestSnapshot = new(
        IsConnected: false,
        IsReady: false,
        HealthDetail: "Mattermost gateway disconnected.",
        BotUserId: null,
        BotUsername: null);

    public event Func<MattermostGatewayMessage, Task>? MessageReceived;
    public event Func<string, Task>? CleanReconnectRequired;
    public event Func<MattermostGatewaySnapshot, Task>? ConnectionRestored;

    public bool IsConnected => _latestSnapshot.IsConnected;
    public bool IsReady => _latestSnapshot.IsReady;
    public MattermostUserId? BotUserId => _latestSnapshot.BotUserId;
    public string? BotUsername => _latestSnapshot.BotUsername;

    public MattermostNetGatewayClient(
        ActorSystem actorSystem,
        MattermostClient client,
        TimeProvider timeProvider,
        ILogger<MattermostNetGatewayClient> logger)
    {
        _actorSystem = actorSystem;
        _lifecycleActor = actorSystem.ActorOf(
            MattermostNetGatewayLifecycleActor.CreateProps(
                new MattermostNetGatewayTransport(client, timeProvider, logger),
                timeProvider,
                this,
                logger),
            "mattermost-net-gateway-lifecycle");
    }

    public async Task<MattermostGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        UpdateSnapshot(await _lifecycleActor.Ask<MattermostGatewaySnapshot>(
            MattermostNetGatewayLifecycleActor.GetSnapshot.Instance,
            SnapshotAskTimeout,
            cancellationToken: cancellationToken));

    public async Task<MattermostGatewaySnapshot> ConnectAsync(
        string serverUrl,
        string botToken,
        CancellationToken cancellationToken = default) =>
        UpdateSnapshot(await _lifecycleActor.Ask<MattermostGatewaySnapshot>(
            new MattermostNetGatewayLifecycleActor.Connect(serverUrl, botToken),
            ConnectAskTimeout,
            cancellationToken: cancellationToken));

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        UpdateSnapshot(await _lifecycleActor.Ask<MattermostGatewaySnapshot>(
            MattermostNetGatewayLifecycleActor.Disconnect.Instance,
            ConnectAskTimeout,
            cancellationToken: cancellationToken));
    }

    public void Dispose() => _actorSystem.Stop(_lifecycleActor);

    private MattermostGatewaySnapshot UpdateSnapshot(MattermostGatewaySnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        return snapshot;
    }

    Task IMattermostGatewayEventSink.PublishMessageAsync(MattermostGatewayMessage message) =>
        MessageReceived?.Invoke(message) ?? Task.CompletedTask;

    Task IMattermostGatewayEventSink.PublishCleanReconnectRequiredAsync(string reason) =>
        CleanReconnectRequired?.Invoke(reason) ?? Task.CompletedTask;

    Task IMattermostGatewayEventSink.PublishConnectionRestoredAsync(MattermostGatewaySnapshot snapshot)
    {
        UpdateSnapshot(snapshot);
        return ConnectionRestored?.Invoke(snapshot) ?? Task.CompletedTask;
    }
}

internal sealed class MattermostNetGatewayTransport : IMattermostGatewayTransport
{
    private readonly MattermostClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _subscriptionLock = new();

    private Func<MattermostGatewayMessage, Task>? _messageReceived;
    private Func<Task>? _connected;
    private Func<MattermostGatewayDisconnect, Task>? _disconnected;
    private Func<string, Task>? _logReceived;
    private int _eventSubscriptionCount;
    private string? _serverUrl;
    private string? _botUserId;
    private string? _botUsername;

    public MattermostNetGatewayTransport(
        MattermostClient client,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _client = client;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public event Func<MattermostGatewayMessage, Task> MessageReceived
    {
        add
        {
            _messageReceived += value;
            AddSdkSubscription();
        }
        remove
        {
            _messageReceived -= value;
            RemoveSdkSubscription();
        }
    }

    public event Func<Task> Connected
    {
        add
        {
            _connected += value;
            AddSdkSubscription();
        }
        remove
        {
            _connected -= value;
            RemoveSdkSubscription();
        }
    }

    public event Func<MattermostGatewayDisconnect, Task> Disconnected
    {
        add
        {
            _disconnected += value;
            AddSdkSubscription();
        }
        remove
        {
            _disconnected -= value;
            RemoveSdkSubscription();
        }
    }

    public event Func<string, Task> LogReceived
    {
        add
        {
            _logReceived += value;
            AddSdkSubscription();
        }
        remove
        {
            _logReceived -= value;
            RemoveSdkSubscription();
        }
    }

    public bool IsConnected => _client.IsConnected;

    public async Task<MattermostBotIdentity> StartAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        // First layer of bot self-dedup: the SDK refuses to surface our own
        // posts at all. The second layer (IsBotMessage tagging below at the
        // MattermostGatewayMessage construction site) defends against a future
        // SDK option default flip or a server-side replay that bypasses the
        // SDK filter — Slack does the same double-check.
        _client.Options.IgnoreOwnMessages = true;

        var me = await _client.GetMeAsync();
        _botUserId = me.Id;
        _botUsername = me.Username;
        _logger.LogInformation("Bot identity resolved: {BotUserId} (@{Username})",
            me.Id, me.Username);

        await _client.StartReceivingAsync(cancellationToken);
        return new MattermostBotIdentity(me.Id, me.Username);
    }

    public async Task StopAsync()
    {
        if (!_client.IsConnected)
            return;

        try
        {
            await _client.StopReceivingAsync();
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.InvalidState)
        {
            _logger.LogDebug(ex, "WebSocket was already closed during disconnect.");
        }
    }

    private void AddSdkSubscription()
    {
        lock (_subscriptionLock)
        {
            if (_eventSubscriptionCount++ == 0)
                SubscribeSdkEvents();
        }
    }

    private void RemoveSdkSubscription()
    {
        lock (_subscriptionLock)
        {
            _eventSubscriptionCount--;
            if (_eventSubscriptionCount == 0)
                UnsubscribeSdkEvents();
        }
    }

    private void SubscribeSdkEvents()
    {
        _client.OnMessageReceived += OnMessageReceived;
        _client.OnConnected += OnConnected;
        _client.OnDisconnected += OnDisconnected;
        _client.OnLogMessage += OnLogMessage;
    }

    private void UnsubscribeSdkEvents()
    {
        _client.OnMessageReceived -= OnMessageReceived;
        _client.OnConnected -= OnConnected;
        _client.OnDisconnected -= OnDisconnected;
        _client.OnLogMessage -= OnLogMessage;
    }

    private void OnMessageReceived(object? sender, MessageEventArgs e)
    {
        var handler = _messageReceived;
        if (handler is null)
            return;

        if (_serverUrl is null)
        {
            _logger.LogWarning(
                "Dropping Mattermost message {PostId} before gateway server URL is configured.",
                e.Message.Post.Id);
            return;
        }

        var post = e.Message.Post;
        var channelType = e.Message.ChannelType;
        var isDm = string.Equals(channelType, "D", StringComparison.Ordinal);

        var botId = _botUserId;
        var botUsername = _botUsername ?? e.Client.CurrentUserInfo.Username;
        var containsMention = botId is not null
            && !string.IsNullOrWhiteSpace(botUsername)
            && !string.IsNullOrEmpty(post.Text)
            && post.Text.Contains($"@{botUsername}", StringComparison.OrdinalIgnoreCase);

        // Mentions field is a JSON array of user IDs
        if (!containsMention && botId is not null && !string.IsNullOrEmpty(e.Message.Mentions))
        {
            containsMention = e.Message.Mentions.Contains(botId, StringComparison.Ordinal);
        }

        var rootPostId = string.IsNullOrEmpty(post.RootId)
            ? new MattermostRootPostId(string.Empty)
            : new MattermostRootPostId(post.RootId);

        IReadOnlyList<string> fileIds = post.FileIdentifiers as IReadOnlyList<string> ?? post.FileIdentifiers.ToList();
        var serverUrl = _serverUrl;
        var receivedAt = _timeProvider.GetUtcNow();

        _ = Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<MattermostFileReference>? attachments = null;
                if (fileIds.Count > 0)
                    attachments = await ResolveFileReferencesAsync(fileIds, serverUrl);

                var gatewayMessage = new MattermostGatewayMessage(
                    EventId: new MattermostEventId(post.Id),
                    ChannelId: new MattermostChannelId(post.ChannelId),
                    PostId: new MattermostPostId(post.Id),
                    RootPostId: rootPostId,
                    SenderId: new MattermostUserId(post.UserId),
                    // Second-layer bot self-dedup. The SDK's IgnoreOwnMessages
                    // filter is the first layer; this tag lets the conversation
                    // actor drop anything that slipped through (e.g. SDK option
                    // regression, server-side replay). Matches Slack's double
                    // check (BotId field AND UserId == BotUserId).
                    IsBotMessage: botId is not null
                        && string.Equals(post.UserId, botId, StringComparison.Ordinal),
                    IsDirectMessage: isDm,
                    ContainsBotMention: containsMention,
                    Text: post.Text ?? string.Empty,
                    ReceivedAt: receivedAt,
                    Attachments: attachments);

                await handler(gatewayMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling Mattermost message {PostId}", post.Id);
            }
        });
    }

    private void OnConnected(object? sender, ConnectionEventArgs e)
    {
        _logger.LogInformation("Connected to Mattermost WebSocket at {Uri}", e.Uri);
        Dispatch("Mattermost connected event", () => _connected?.Invoke() ?? Task.CompletedTask);
    }

    private void OnDisconnected(object? sender, DisconnectionEventArgs e)
    {
        _logger.LogWarning("Disconnected from Mattermost WebSocket: {Reason}", e.CloseStatusDescription);
        Dispatch(
            "Mattermost disconnected event",
            () => _disconnected?.Invoke(new MattermostGatewayDisconnect(e.CloseStatusDescription)) ?? Task.CompletedTask);
    }

    private void OnLogMessage(object? sender, LogEventArgs e)
    {
        Dispatch("Mattermost log event", () => _logReceived?.Invoke(e.Message) ?? Task.CompletedTask);
    }

    private async Task<IReadOnlyList<MattermostFileReference>> ResolveFileReferencesAsync(
        IReadOnlyList<string> fileIds, string serverUrl)
    {
        var tasks = fileIds.Select(async fileId =>
        {
            try
            {
                var details = await _client.GetFileDetailsAsync(fileId);
                return new MattermostFileReference(
                    Name: details.Name ?? fileId,
                    MimeType: details.MimeType ?? "application/octet-stream",
                    Size: details.Size,
                    Url: $"{serverUrl}/api/v4/files/{fileId}");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve file details for {FileId}; using fallback metadata", fileId);
                return new MattermostFileReference(
                    Name: fileId,
                    MimeType: "application/octet-stream",
                    Size: 0,
                    Url: $"{serverUrl}/api/v4/files/{fileId}");
            }
        });

        return await Task.WhenAll(tasks);
    }

    private void Dispatch(string operation, Func<Task> dispatch)
    {
        Task task;
        try
        {
            task = dispatch();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {Operation}", operation);
            return;
        }

        if (!task.IsCompletedSuccessfully)
            _ = AwaitDispatchAsync(operation, task);
    }

    private async Task AwaitDispatchAsync(string operation, Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {Operation}", operation);
        }
    }
}
