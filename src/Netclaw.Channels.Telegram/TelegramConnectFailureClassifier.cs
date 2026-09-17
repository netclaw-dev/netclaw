// -----------------------------------------------------------------------
// <copyright file="TelegramConnectFailureClassifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Telegram.Bot.Exceptions;

namespace Netclaw.Channels.Telegram;

/// <summary>
/// Maps a Telegram startup failure onto a <see cref="ChannelConnectException"/>
/// so the channel host can tell a bad bot token apart from a transient network
/// error. Classification is conservative: only the token rejection the Bot API
/// answers <c>getMe</c> with (HTTP 401) and missing local configuration are
/// fatal — every other failure, including rate limits and unknown errors,
/// retries with backoff.
/// </summary>
internal static class TelegramConnectFailureClassifier
{
    public static ChannelConnectException Classify(Exception failure)
    {
        if (failure is ChannelConnectException already)
            return already;

        // Telegram answers a bad or revoked bot token with HTTP 401 on getMe.
        // Retrying cannot succeed until the operator fixes the token.
        if (failure is ApiRequestException { ErrorCode: 401 } unauthorized)
            return new ChannelConnectException(
                ChannelConnectFailureKind.Fatal,
                "Telegram rejected the bot token (401 Unauthorized). "
                + "Check the Telegram:BotToken secret, then restart the daemon.",
                unauthorized);

        // A missing token fails fast with this message before any network call.
        if (failure is InvalidOperationException { Message: { } message }
            && message.Contains("was not configured", StringComparison.Ordinal))
            return new ChannelConnectException(
                ChannelConnectFailureKind.Fatal,
                $"Telegram channel is misconfigured. {message}",
                failure);

        return new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            $"Telegram connection failed: {failure.Message}",
            failure);
    }
}
