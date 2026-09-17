// -----------------------------------------------------------------------
// <copyright file="TelegramTransportTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramFile = Telegram.Bot.Types.TGFile;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramTransportTests
{
    [Fact]
    public async Task StartAsync_recovers_from_transient_failure_on_second_attempt()
    {
        var failure = new HttpRequestException("The Telegram API is temporarily unreachable.");
        var first = new FakeTelegramBotApiClient { GetMeFailure = failure };
        var second = new FakeTelegramBotApiClient();
        var attempts = new Queue<FakeTelegramBotApiClient>([first, second]);
        var transport = CreateTransport((_, _) => attempts.Dequeue());

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => transport.StartAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        Assert.Equal(1, first.GetMeCalls);
        Assert.Equal(0, first.EventSubscriptionCount);
        Assert.Equal(0, second.GetMeCalls);

        await transport.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, first.GetMeCalls);
        Assert.Equal(0, first.EventSubscriptionCount);
        Assert.Equal(1, second.GetMeCalls);
        Assert.Equal(3, second.EventSubscriptionCount);

        await transport.SendTextAsync(77, "hello", cancellationToken: TestContext.Current.CancellationToken);
        var sent = Assert.Single(second.SentTexts);
        Assert.Equal(77, sent.ChatId);
        Assert.Contains("hello", sent.Text);
    }

    [Fact]
    public async Task Failed_start_leaves_no_active_client_and_stop_is_safe()
    {
        var first = new FakeTelegramBotApiClient { GetMeFailure = new HttpRequestException("flap") };
        var second = new FakeTelegramBotApiClient();
        var attempts = new Queue<FakeTelegramBotApiClient>([first, second]);
        var transport = CreateTransport((_, _) => attempts.Dequeue());

        await Assert.ThrowsAsync<HttpRequestException>(() => transport.StartAsync(TestContext.Current.CancellationToken));

        await transport.StopAsync();
        Assert.Equal(1, first.GetMeCalls);

        await transport.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, second.GetMeCalls);
        Assert.Equal(3, second.EventSubscriptionCount);
    }

    [Fact]
    public async Task StartAsync_while_active_is_rejected_before_a_second_client_is_built()
    {
        var client = new FakeTelegramBotApiClient();
        var builds = 0;
        var transport = CreateTransport((_, _) =>
        {
            builds++;
            return client;
        });

        await transport.StartAsync(TestContext.Current.CancellationToken);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal("The Telegram transport is already active.", thrown.Message);
        Assert.Equal(1, builds);
        Assert.Equal(1, client.GetMeCalls);
    }

    [Fact]
    public async Task Entity_parse_failure_retries_once_as_plain_text()
    {
        var client = new FakeTelegramBotApiClient();
        client.SendMessageFailures.Enqueue(new ApiRequestException(
            "Bad Request: can't parse entities: Unsupported start tag \"netclaw\"", 400));
        var transport = CreateTransport((_, _) => client);

        await transport.StartAsync(TestContext.Current.CancellationToken);
        await transport.SendTextAsync(77, "netclaw **bold** status", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, client.SentTexts.Count);
        Assert.Equal(ParseMode.Html, client.SentTexts[0].ParseMode);
        Assert.Contains("<b>", client.SentTexts[0].Text);
        Assert.NotEqual(ParseMode.Html, client.SentTexts[1].ParseMode);
        Assert.Equal("netclaw **bold** status", client.SentTexts[1].Text);
    }

    [Fact]
    public async Task Authorization_failure_is_not_retried_and_preserves_the_original_exception()
    {
        var client = new FakeTelegramBotApiClient();
        var failure = new ApiRequestException("Unauthorized", 401);
        client.SendMessageFailures.Enqueue(failure);
        var transport = CreateTransport((_, _) => client);

        await transport.StartAsync(TestContext.Current.CancellationToken);
        var thrown = await Assert.ThrowsAsync<ApiRequestException>(
            () => transport.SendTextAsync(77, "hello", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        _ = Assert.Single(client.SentTexts);
    }

    [Fact]
    public async Task Oversized_download_aborts_on_the_crossing_chunk_and_deletes_the_partial_file()
    {
        var staging = Path.Combine(Path.GetTempPath(), $"netclaw-telegram-{Guid.NewGuid():N}");
        try
        {
            var client = new FakeTelegramBotApiClient
            {
                FakeFile = new TGFile { FileId = "file-1", FileSize = 10 },
                DownloadContent = new byte[2048],
                DownloadChunkSize = 512,
            };
            var transport = CreateTransport((_, _) => client);
            await transport.StartAsync(TestContext.Current.CancellationToken);

            var thrown = await Assert.ThrowsAsync<AttachmentTooLargeException>(
                () => transport.DownloadFileAsync(
                    new TelegramFileReference("file-1", "note.bin", "application/octet-stream", 10),
                    staging,
                    maxBytes: 1024,
                    TestContext.Current.CancellationToken));

            Assert.Equal(1024, thrown.MaxBytes);
            Assert.Equal(1536, thrown.BytesReceived);
            Assert.Equal(3, client.DownloadChunkAttempts);
            Assert.Empty(Directory.GetFiles(staging));
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public async Task Under_limit_download_saves_the_complete_file()
    {
        var staging = Path.Combine(Path.GetTempPath(), $"netclaw-telegram-{Guid.NewGuid():N}");
        try
        {
            var content = new byte[600];
            Array.Fill(content, (byte)0xAB);
            var client = new FakeTelegramBotApiClient
            {
                FakeFile = new TGFile { FileId = "file-1", FileSize = 600 },
                DownloadContent = content,
                DownloadChunkSize = 512,
            };
            var transport = CreateTransport((_, _) => client);
            await transport.StartAsync(TestContext.Current.CancellationToken);

            var result = await transport.DownloadFileAsync(
                new TelegramFileReference("file-1", "note.bin", "application/octet-stream", 600),
                staging,
                maxBytes: 1024,
                TestContext.Current.CancellationToken);

            Assert.Equal(600, result.BytesWritten);
            Assert.True(File.Exists(result.FilePath));
            var saved = await File.ReadAllBytesAsync(result.FilePath, TestContext.Current.CancellationToken);
            Assert.Equal(content, saved);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static TelegramTransport CreateTransport(TelegramBotClientFactory factory) =>
        new(ValidOptions(), NullLogger<TelegramTransport>.Instance, factory);

    private static TelegramChannelOptions ValidOptions() => new()
    {
        Enabled = true,
        BotToken = new SensitiveString("12345:test-token"),
    };

    internal sealed class FakeTelegramBotApiClient : ITelegramBotApiClient
    {
        public Exception? GetMeFailure { get; set; }

        public int GetMeCalls { get; private set; }

        public int EventSubscriptionCount { get; private set; }

        public List<(long ChatId, string Text, ParseMode ParseMode, int? MessageThreadId)> SentTexts { get; } = [];

        public Queue<Exception> SendMessageFailures { get; } = new();

        public TGFile? FakeFile { get; set; }

        public byte[] DownloadContent { get; set; } = [];

        public int DownloadChunkSize { get; set; } = 512;

        public int DownloadChunkAttempts { get; private set; }

        private TelegramBotClient.OnMessageHandler? _onMessage;

        private TelegramBotClient.OnUpdateHandler? _onUpdate;

        private TelegramBotClient.OnErrorHandler? _onError;

        public event TelegramBotClient.OnMessageHandler OnMessage
        {
            add { _onMessage += value; EventSubscriptionCount++; }
            remove { _onMessage -= value; EventSubscriptionCount--; }
        }

        public event TelegramBotClient.OnUpdateHandler OnUpdate
        {
            add { _onUpdate += value; EventSubscriptionCount++; }
            remove { _onUpdate -= value; EventSubscriptionCount--; }
        }

        public event TelegramBotClient.OnErrorHandler OnError
        {
            add { _onError += value; EventSubscriptionCount++; }
            remove { _onError -= value; EventSubscriptionCount--; }
        }

        public Task<User> GetMe(CancellationToken cancellationToken = default)
        {
            GetMeCalls++;
            if (GetMeFailure is not null)
                throw GetMeFailure;

            return Task.FromResult(new User { Id = 4242, IsBot = true, Username = "testbot" });
        }

        public Task SendChatAction(long chatId, ChatAction action, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Message> SendMessage(
            long chatId,
            string text,
            ParseMode parseMode = default,
            InlineKeyboardMarkup? replyMarkup = null,
            int? messageThreadId = null,
            CancellationToken cancellationToken = default)
        {
            SentTexts.Add((chatId, text, parseMode, messageThreadId));
            if (SendMessageFailures.TryDequeue(out var failure))
                throw failure;

            return Task.FromResult(new Message { Id = 1 });
        }

        public Task<Message> SendRichMessage(
            long chatId,
            InputRichMessage message,
            int? messageThreadId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Message> SendDocument(
            long chatId,
            InputFile file,
            int? messageThreadId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task AnswerCallbackQuery(string queryId, string? text, bool showAlert, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EditMessageText(
            long chatId,
            int messageId,
            string text,
            ParseMode parseMode = default,
            InlineKeyboardMarkup? replyMarkup = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<TelegramFile> GetFile(string fileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(FakeFile ?? new TGFile { FileId = fileId });

        public async Task DownloadFile(TelegramFile file, Stream destination, CancellationToken cancellationToken = default)
        {
            for (var offset = 0; offset < DownloadContent.Length; offset += DownloadChunkSize)
            {
                var count = Math.Min(DownloadChunkSize, DownloadContent.Length - offset);
                DownloadChunkAttempts++;
                await destination.WriteAsync(DownloadContent.AsMemory(offset, count), cancellationToken);
            }
        }
    }
}
