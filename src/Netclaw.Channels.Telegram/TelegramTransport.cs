// -----------------------------------------------------------------------
// <copyright file="TelegramTransport.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Netclaw.Channels.Telegram;

public sealed class TelegramTransport(
    TelegramChannelOptions options,
    ILogger<TelegramTransport> logger,
    TelegramBotClientFactory clientFactory) : IAsyncDisposable
{
    private readonly object _albumLock = new();
    private readonly Dictionary<string, AlbumBuffer> _albums = [];
    private CancellationTokenSource? _stopSource;
    private ITelegramBotApiClient? _client;
    private long? _botUserId;
    private string? _botUsername;

    public event Func<TelegramInboundMessage, Task>? MessageReceived;

    public event Func<TelegramCallbackQuery, Task>? CallbackReceived;

    public event Func<string, Task>? PollingFailed;

    public event Func<Task>? PollingRecovered;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
            throw new InvalidOperationException("The Telegram transport is already active.");

        if (!options.Enabled)
            throw new InvalidOperationException("The Telegram channel is disabled.");

        var token = options.BotToken.RequireValid("Telegram bot token");
        var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var client = clientFactory(token.Value, stopSource.Token);
        try
        {
            var bot = await client.GetMe(stopSource.Token).ConfigureAwait(false);
            client.OnMessage += HandleMessageAsync;
            client.OnUpdate += HandleUpdateAsync;
            client.OnError += HandleErrorAsync;
            _botUserId = bot.Id;
            _botUsername = bot.Username;
            _client = client;
            _stopSource = stopSource;
        }
        catch
        {
            // Nothing is committed on a failed start. The linked CTS is the
            // only owned resource to release; dropping the client lets the
            // next StartAsync build a fresh one instead of tripping the
            // already-active guard forever.
            stopSource.Dispose();
            throw;
        }
    }

    public Task StopAsync()
    {
        if (_client is not null)
        {
            _client.OnMessage -= HandleMessageAsync;
            _client.OnUpdate -= HandleUpdateAsync;
            _client.OnError -= HandleErrorAsync;
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
        int? messageThreadId = null,
        CancellationToken cancellationToken = default)
    {
        var client = _client
            ?? throw new InvalidOperationException("The Telegram transport is not active.");

        var rich = TelegramTextFormatter.ToRichHtml(text);
        if (rich.ContainsTable)
        {
            try
            {
                await client.SendRichMessage(
                    chatId,
                    new InputRichMessage { Html = rich.Html },
                    messageThreadId,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ApiRequestException ex)
            {
                // A rejected rich message must still deliver its content. Log loudly so a
                // Telegram API regression cannot silently degrade every table reply.
                logger.LogWarning(
                    ex,
                    "Telegram rejected the rich table message for chat {ChatId}; falling back to flattened text.",
                    chatId);
            }
        }

        try
        {
            await client.SendMessage(
                chatId,
                TelegramTextFormatter.ToHtml(text),
                parseMode: ParseMode.Html,
                messageThreadId: messageThreadId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("can't parse entities", StringComparison.Ordinal))
        {
            // Only Telegram's documented entity-parse rejection may fall back to
            // plain text. Auth failures, rate limits, and bad destinations keep
            // the original failure — a retry cannot succeed and only hides the cause.
            await client.SendMessage(
                chatId,
                text,
                messageThreadId: messageThreadId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SendDocumentAsync(
        long chatId,
        string filePath,
        string fileName,
        int? messageThreadId = null,
        CancellationToken cancellationToken = default)
    {
        var client = _client
            ?? throw new InvalidOperationException("The Telegram transport is not active.");

        await using var stream = File.OpenRead(filePath);
        await client.SendDocument(
            chatId,
            InputFile.FromStream(stream, fileName),
            messageThreadId: messageThreadId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> SendApprovalPromptAsync(
        long chatId,
        string text,
        IReadOnlyList<TelegramApprovalButton> buttons,
        int? messageThreadId = null,
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
            messageThreadId: messageThreadId,
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

            // Fast-fail on the provider-reported size when present. Telegram may
            // omit or misreport it, so this is an optimization only — the
            // bounded stream below is the actual enforcement.
            if (telegramFile.FileSize is { } reportedSize && reportedSize > maxBytes)
                throw new AttachmentTooLargeException(reportedSize, maxBytes);

            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var bounded = new BoundedWriteStream(stream, maxBytes);
            await client.DownloadFile(telegramFile, bounded, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AttachmentDownloadResult(path, bounded.TotalWritten);
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
        await NotifyPollingRecoveredAsync().ConfigureAwait(false);
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
            message.ReplyToMessage?.From?.Id == _botUserId,
            message.MessageThreadId);

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

    private async Task HandleUpdateAsync(Update update)
    {
        await NotifyPollingRecoveredAsync().ConfigureAwait(false);
        if (update.CallbackQuery is not { Message: { } message, Data: { Length: > 0 } data } callback)
            return;

        var inbound = new TelegramCallbackQuery(
            message.Chat.Id,
            callback.From.Id,
            message.Id,
            callback.Id,
            data,
            message.MessageThreadId);
        if (CallbackReceived is { } handler)
            await handler(inbound).ConfigureAwait(false);
        else
            await AnswerCallbackAsync(callback.Id, "This action is unavailable.", showAlert: true).ConfigureAwait(false);
    }

    private Task HandleErrorAsync(Exception exception, HandleErrorSource source)
    {
        var detail = $"Telegram polling failed ({source}): {exception.Message}";
        logger.LogWarning(exception, "{Detail}", detail);
        return PollingFailed is { } handler ? handler(detail) : Task.CompletedTask;
    }

    private Task NotifyPollingRecoveredAsync() =>
        PollingRecovered is { } handler ? handler() : Task.CompletedTask;

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

    /// <summary>
    /// Write-through stream that rejects the chunk crossing <paramref
    /// name="maxBytes"/> before it reaches disk. Telegram's reported file size
    /// can be false or absent, so the SDK download must abort mid-copy instead
    /// of checking the size after the whole file has landed. Same semantics as
    /// <see cref="StreamingAttachmentDownloader"/>.
    /// </summary>
    private sealed class BoundedWriteStream(Stream inner, long maxBytes) : Stream
    {
        private long _totalWritten;

        public long TotalWritten => _totalWritten;

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureWithinLimit(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _totalWritten += buffer.Length;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinLimit(count);
            inner.Write(buffer, offset, count);
            _totalWritten += count;
        }

        private void EnsureWithinLimit(int incoming)
        {
            if (_totalWritten + incoming > maxBytes)
                throw new AttachmentTooLargeException(_totalWritten + incoming, maxBytes);
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
