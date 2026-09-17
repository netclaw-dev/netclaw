// -----------------------------------------------------------------------
// <copyright file="TelegramBotApiClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramFile = Telegram.Bot.Types.TGFile;

namespace Netclaw.Channels.Telegram;

/// <summary>
/// Creates one API client per start attempt. The polling lifetime token must
/// reach the client constructor, so the transport owns client creation
/// instead of the DI container.
/// </summary>
public delegate ITelegramBotApiClient TelegramBotClientFactory(
    string botToken,
    CancellationToken pollingLifetime);

/// <summary>
/// The bot API surface that <see cref="TelegramTransport"/> consumes. The SDK
/// exposes its API methods as extension methods and its polling events only on
/// the concrete <see cref="TelegramBotClient"/>, so a test fake cannot
/// subclass or implement the SDK type directly. Tests fake this wrapper.
/// </summary>
public interface ITelegramBotApiClient
{
    event TelegramBotClient.OnMessageHandler OnMessage;

    event TelegramBotClient.OnUpdateHandler OnUpdate;

    event TelegramBotClient.OnErrorHandler OnError;

    Task<User> GetMe(CancellationToken cancellationToken = default);

    Task SendChatAction(long chatId, ChatAction action, CancellationToken cancellationToken = default);

    Task<Message> SendMessage(
        long chatId,
        string text,
        ParseMode parseMode = default,
        InlineKeyboardMarkup? replyMarkup = null,
        int? messageThreadId = null,
        CancellationToken cancellationToken = default);

    Task<Message> SendRichMessage(
        long chatId,
        InputRichMessage message,
        int? messageThreadId = null,
        CancellationToken cancellationToken = default);

    Task<Message> SendDocument(
        long chatId,
        InputFile file,
        int? messageThreadId = null,
        CancellationToken cancellationToken = default);

    Task AnswerCallbackQuery(string queryId, string? text, bool showAlert, CancellationToken cancellationToken = default);

    Task EditMessageText(
        long chatId,
        int messageId,
        string text,
        ParseMode parseMode = default,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default);

    Task<TelegramFile> GetFile(string fileId, CancellationToken cancellationToken = default);

    Task DownloadFile(TelegramFile file, Stream destination, CancellationToken cancellationToken = default);
}

/// <summary>
/// Production adapter that forwards to a real <see cref="TelegramBotClient"/>
/// whose polling loop lives exactly as long as the supplied token.
/// </summary>
internal sealed class TelegramBotApiClient : ITelegramBotApiClient
{
    private readonly TelegramBotClient _client;

    public TelegramBotApiClient(string botToken, CancellationToken pollingLifetime) =>
        _client = new TelegramBotClient(botToken, cancellationToken: pollingLifetime);

    public static TelegramBotClientFactory Factory { get; } =
        (botToken, pollingLifetime) => new TelegramBotApiClient(botToken, pollingLifetime);

    public event TelegramBotClient.OnMessageHandler OnMessage
    {
        add => _client.OnMessage += value;
        remove => _client.OnMessage -= value;
    }

    public event TelegramBotClient.OnUpdateHandler OnUpdate
    {
        add => _client.OnUpdate += value;
        remove => _client.OnUpdate -= value;
    }

    public event TelegramBotClient.OnErrorHandler OnError
    {
        add => _client.OnError += value;
        remove => _client.OnError -= value;
    }

    public Task<User> GetMe(CancellationToken cancellationToken = default) =>
        _client.GetMe(cancellationToken);

    public Task SendChatAction(long chatId, ChatAction action, CancellationToken cancellationToken = default) =>
        _client.SendChatAction(chatId, action, cancellationToken: cancellationToken);

    public Task<Message> SendMessage(
        long chatId,
        string text,
        ParseMode parseMode = default,
        InlineKeyboardMarkup? replyMarkup = null,
        int? messageThreadId = null,
        CancellationToken cancellationToken = default) =>
        _client.SendMessage(chatId, text, parseMode: parseMode, replyMarkup: replyMarkup, messageThreadId: messageThreadId, cancellationToken: cancellationToken);

    public Task<Message> SendRichMessage(
        long chatId,
        InputRichMessage message,
        int? messageThreadId = null,
        CancellationToken cancellationToken = default) =>
        _client.SendRichMessage(chatId, message, messageThreadId: messageThreadId, cancellationToken: cancellationToken);

    public Task<Message> SendDocument(
        long chatId,
        InputFile file,
        int? messageThreadId = null,
        CancellationToken cancellationToken = default) =>
        _client.SendDocument(chatId, file, messageThreadId: messageThreadId, cancellationToken: cancellationToken);

    public Task AnswerCallbackQuery(string queryId, string? text, bool showAlert, CancellationToken cancellationToken = default) =>
        _client.AnswerCallbackQuery(queryId, text, showAlert, cancellationToken: cancellationToken);

    public Task EditMessageText(
        long chatId,
        int messageId,
        string text,
        ParseMode parseMode = default,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default) =>
        _client.EditMessageText(chatId, messageId, text, parseMode: parseMode, replyMarkup: replyMarkup, cancellationToken: cancellationToken);

    public Task<TelegramFile> GetFile(string fileId, CancellationToken cancellationToken = default) =>
        _client.GetFile(fileId, cancellationToken);

    public Task DownloadFile(TelegramFile file, Stream destination, CancellationToken cancellationToken = default) =>
        _client.DownloadFile(file, destination, cancellationToken);
}
