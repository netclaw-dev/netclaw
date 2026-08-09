// -----------------------------------------------------------------------
// <copyright file="TelegramTransport.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Netclaw.Channels.Telegram;

public sealed class TelegramTransport(
    TelegramChannelOptions options,
    ILogger<TelegramTransport> logger) : IAsyncDisposable
{
    private readonly object _albumLock = new();
    private readonly Dictionary<string, AlbumBuffer> _albums = [];
    private CancellationTokenSource? _stopSource;
    private TelegramBotClient? _client;
    private long? _botUserId;
    private string? _botUsername;

    public event Func<TelegramInboundMessage, Task>? MessageReceived;

    public event Func<TelegramCallbackQuery, Task>? CallbackReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
            throw new InvalidOperationException("The Telegram transport is already active.");

        if (!options.Enabled)
            throw new InvalidOperationException("The Telegram channel is disabled.");

        var token = options.BotToken.RequireValid("Telegram bot token");
        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _client = new TelegramBotClient(token.Value, cancellationToken: _stopSource.Token);

        var bot = await _client.GetMe(_stopSource.Token).ConfigureAwait(false);
        _botUserId = bot.Id;
        _botUsername = bot.Username;
        _client.OnMessage += HandleMessageAsync;
        _client.OnUpdate += HandleUpdateAsync;
    }

    public Task StopAsync()
    {
        if (_client is not null)
        {
            _client.OnMessage -= HandleMessageAsync;
            _client.OnUpdate -= HandleUpdateAsync;
        }

        _stopSource?.Cancel();
        _stopSource?.Dispose();
        _stopSource = null;
        _client = null;
        _botUserId = null;
        _botUsername = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    public async Task SendChatActionAsync(
        long chatId,
        ChatAction chatAction,
        CancellationToken cancellationToken = default)
    {
        var client = _client
            ?? throw new InvalidOperationException("The Telegram transport is not active.");
        await client.SendChatAction(chatId, chatAction, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SendTextAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var client = _client
            ?? throw new InvalidOperationException("The Telegram transport is not active.");

        try
        {
            await client.SendMessage(
                chatId,
                TelegramTextFormatter.ToHtml(text),
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException)
        {
            // Formatting must never prevent the user from receiving the answer.
            await client.SendMessage(chatId, text, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SendDocumentAsync(
        long chatId,
        string filePath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var client = _client
            ?? throw new InvalidOperationException("The Telegram transport is not active.");

        await using var stream = File.OpenRead(filePath);
        await client.SendDocument(
            chatId,
            InputFile.FromStream(stream, fileName),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> SendApprovalPromptAsync(
        long chatId,
        string text,
        IReadOnlyList<TelegramApprovalButton> buttons,
        CancellationToken cancellationToken = default)
    {
        var client = _client
            ?? throw new InvalidOperationException("The Telegram transport is not active.");
        var rows = buttons
            .Chunk(2)
            .Select(row => row.Select(button =>
                InlineKeyboardButton.WithCallbackData(button.Label, button.CallbackData)));
        var markup = new InlineKeyboardMarkup(rows);
        var message = await client.SendMessage(
            chatId,
            TelegramTextFormatter.ToHtml(text),
            parseMode: ParseMode.Html,
            replyMarkup: markup,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return message.Id;
    }

    public async Task AnswerCallbackAsync(
        string queryId,
        string? text = null,
        bool showAlert = false,
        CancellationToken cancellationToken = default)
    {
        var client = _client
            ?? throw new InvalidOperationException("The Telegram transport is not active.");
        await client.AnswerCallbackQuery(
            queryId,
            text,
            showAlert,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateApprovalPromptAsync(
        long chatId,
        int messageId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var client = _client
            ?? throw new InvalidOperationException("The Telegram transport is not active.");
        await client.EditMessageText(
            chatId,
            messageId,
            TelegramTextFormatter.ToHtml(text),
            parseMode: ParseMode.Html,
            replyMarkup: InlineKeyboardMarkup.Empty(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttachmentDownloadResult> DownloadFileAsync(
        TelegramFileReference file,
        string stagingDirectory,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var client = _client
            ?? throw new InvalidOperationException("The Telegram transport is not active.");
        Directory.CreateDirectory(stagingDirectory);
        var path = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.download");

        try
        {
            var telegramFile = await client.GetFile(file.FileId, cancellationToken).ConfigureAwait(false);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await client.DownloadFile(telegramFile, stream, cancellationToken).ConfigureAwait(false);
            if (stream.Length > maxBytes)
                throw new AttachmentTooLargeException(stream.Length, maxBytes);
            return new AttachmentDownloadResult(path, stream.Length);
        }
        catch
        {
            if (File.Exists(path))
                File.Delete(path);
            throw;
        }
    }

    private async Task HandleMessageAsync(Message message, UpdateType updateType)
    {
        var files = MapFiles(message);
        var text = message.Text ?? message.Caption ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) && files.Count == 0)
            return;

        var inbound = new TelegramInboundMessage(
            message.Chat.Id,
            message.From?.Id,
            message.Id,
            text,
            message.Chat.Type == ChatType.Private,
            files,
            ContainsBotMention(message, text),
            message.ReplyToMessage?.From?.Id == _botUserId);

        var aclDecision = TelegramAclPolicy.EvaluateInbound(inbound, options);
        if (!aclDecision.IsAllowed)
        {
            logger.LogInformation(
                "Telegram message rejected by ACL. ChatId={ChatId} UserId={UserId} Reason={Reason}",
                inbound.ChatId,
                inbound.UserId,
                aclDecision.DenyReason ?? "acl_denied");
            return;
        }

        if (message.MediaGroupId is not { Length: > 0 } albumId)
        {
            await PublishAsync(inbound).ConfigureAwait(false);
            return;
        }

        QueueAlbum(albumId, inbound);
    }

    private Task HandleUpdateAsync(Update update)
    {
        if (update.CallbackQuery is not { Message: { } message, Data: { Length: > 0 } data } callback)
            return Task.CompletedTask;

        var inbound = new TelegramCallbackQuery(
            message.Chat.Id,
            callback.From.Id,
            message.Id,
            callback.Id,
            data);
        return CallbackReceived is { } handler
            ? handler(inbound)
            : AnswerCallbackAsync(callback.Id, "This action is unavailable.", showAlert: true);
    }

    private bool ContainsBotMention(Message message, string text)
    {
        var entities = message.Text is not null
            ? message.Entities
            : message.CaptionEntities;

        if (entities is null)
            return false;

        foreach (var entity in entities)
        {
            if (entity.Type == MessageEntityType.TextMention
                && entity.User?.Id == _botUserId)
                return true;

            if (entity.Type != MessageEntityType.Mention
                || string.IsNullOrWhiteSpace(_botUsername)
                || entity.Offset < 0
                || entity.Length <= 0
                || entity.Offset + entity.Length > text.Length)
                continue;

            var mention = text.Substring(entity.Offset, entity.Length);
            if (string.Equals(mention, $"@{_botUsername}", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private Task PublishAsync(TelegramInboundMessage message) =>
        MessageReceived is { } handler ? handler(message) : Task.CompletedTask;

    private void QueueAlbum(string albumId, TelegramInboundMessage message)
    {
        CancellationToken token;
        lock (_albumLock)
        {
            if (!_albums.TryGetValue(albumId, out var album))
            {
                album = new AlbumBuffer(message);
                _albums.Add(albumId, album);
            }
            else
            {
                album.Messages.Add(message);
                album.Delay.Cancel();
                album.Delay.Dispose();
                album.Delay = new CancellationTokenSource();
            }

            token = album.Delay.Token;
        }

        _ = FlushAlbumAsync(albumId, token);
    }

    private async Task FlushAlbumAsync(string albumId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        AlbumBuffer? album;
        lock (_albumLock)
        {
            if (!_albums.Remove(albumId, out album))
                return;
        }

        using (album.Delay)
        {
            var first = album.Messages[0];
            var combined = first with
            {
                Text = album.Messages.Select(item => item.Text)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                Files = album.Messages.SelectMany(item => item.Files ?? []).ToArray()
            };
            await PublishAsync(combined).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<TelegramFileReference> MapFiles(Message message)
    {
        if (message.Document is { } document)
        {
            return [new TelegramFileReference(
                document.FileId,
                document.FileName ?? $"telegram-{message.Id}",
                document.MimeType ?? "application/octet-stream",
                document.FileSize ?? 0)];
        }

        if (message.Photo is { Length: > 0 } photos)
        {
            var photo = photos[^1];
            return [new TelegramFileReference(
                photo.FileId,
                $"telegram-photo-{message.Id}.jpg",
                "image/jpeg",
                photo.FileSize ?? 0)];
        }

        return [];
    }

    private sealed class AlbumBuffer(TelegramInboundMessage first)
    {
        public List<TelegramInboundMessage> Messages { get; } = [first];

        public CancellationTokenSource Delay { get; set; } = new();
    }
}
