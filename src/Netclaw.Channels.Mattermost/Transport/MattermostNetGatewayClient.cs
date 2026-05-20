// -----------------------------------------------------------------------
// <copyright file="MattermostNetGatewayClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost;
using Mattermost.Events;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;

namespace Netclaw.Channels.Mattermost.Transport;

internal sealed class MattermostNetGatewayClient : IMattermostGatewayClient, IDisposable
{
    private readonly MattermostClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MattermostNetGatewayClient> _logger;

    private string? _serverUrl;

    public event Func<MattermostGatewayMessage, Task>? MessageReceived;

    public bool IsConnected => _client.IsConnected;
    public MattermostUserId? BotUserId { get; private set; }
    public string? BotUsername { get; private set; }

    public MattermostNetGatewayClient(
        MattermostClient client,
        TimeProvider timeProvider,
        ILogger<MattermostNetGatewayClient> logger)
    {
        _client = client;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ConnectAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        // First layer of bot self-dedup: the SDK refuses to surface our own
        // posts at all. The second layer (IsBotMessage tagging below at the
        // MattermostGatewayMessage construction site) defends against a future
        // SDK option default flip or a server-side replay that bypasses the
        // SDK filter — Slack does the same double-check.
        _client.Options.IgnoreOwnMessages = true;

        _client.OnMessageReceived += OnMessageReceived;
        _client.OnConnected += OnConnected;
        _client.OnDisconnected += OnDisconnected;
        _client.OnLogMessage += OnLogMessage;

        var me = await _client.GetMeAsync();
        BotUserId = new MattermostUserId(me.Id);
        BotUsername = me.Username;
        _logger.LogInformation("Mattermost bot identity resolved: {BotUserId} (@{Username})",
            me.Id, me.Username);

        await _client.StartReceivingAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
            return;

        try
        {
            await _client.StopReceivingAsync();
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.InvalidState)
        {
            _logger.LogDebug(ex, "Mattermost WebSocket was already closed during disconnect.");
        }
    }

    private void OnMessageReceived(object? sender, MessageEventArgs e)
    {
        var handler = MessageReceived;
        if (handler is null)
            return;

        var post = e.Message.Post;
        var channelType = e.Message.ChannelType;
        var isDm = string.Equals(channelType, "D", StringComparison.Ordinal);

        var botId = BotUserId?.Value;
        var containsMention = botId is not null
            && !string.IsNullOrEmpty(post.Text)
            && post.Text.Contains($"@{e.Client.CurrentUserInfo.Username}", StringComparison.OrdinalIgnoreCase);

        // Mentions field is a JSON array of user IDs
        if (!containsMention && botId is not null && !string.IsNullOrEmpty(e.Message.Mentions))
        {
            containsMention = e.Message.Mentions.Contains(botId, StringComparison.Ordinal);
        }

        var rootPostId = string.IsNullOrEmpty(post.RootId)
            ? new MattermostRootPostId(string.Empty)
            : new MattermostRootPostId(post.RootId);

        IReadOnlyList<string> fileIds = post.FileIdentifiers as IReadOnlyList<string> ?? post.FileIdentifiers.ToList();
        var serverUrl = _serverUrl!;
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
    }

    private void OnDisconnected(object? sender, DisconnectionEventArgs e)
    {
        _logger.LogWarning("Disconnected from Mattermost WebSocket: {Reason}", e.CloseStatusDescription);
    }

    private void OnLogMessage(object? sender, LogEventArgs e)
    {
        _logger.LogDebug("[Mattermost.NET] {Message}", e.Message);
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

    public void Dispose()
    {
        _client.OnMessageReceived -= OnMessageReceived;
        _client.OnConnected -= OnConnected;
        _client.OnDisconnected -= OnDisconnected;
        _client.OnLogMessage -= OnLogMessage;
        // Do not dispose the MattermostClient — it's owned by the DI container.
    }
}
