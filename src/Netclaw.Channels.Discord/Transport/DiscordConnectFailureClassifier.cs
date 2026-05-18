// -----------------------------------------------------------------------
// <copyright file="DiscordConnectFailureClassifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Discord.Net;

namespace Netclaw.Channels.Discord.Transport;

/// <summary>
/// Maps a Discord.Net connection failure onto a <see cref="ChannelConnectException"/>
/// so the channel host can tell a fixable misconfiguration (bad token, disallowed
/// intents) apart from a transient network/gateway error.
/// </summary>
internal static class DiscordConnectFailureClassifier
{
    // Gateway close codes that signal a configuration/permission problem an
    // operator must fix. Discord.Net does not reconnect on these, so retrying
    // would loop forever — the channel stays disconnected until config is fixed.
    // Ref: https://discord.com/developers/docs/topics/opcodes-and-status-codes#gateway-gateway-close-event-codes
    private static readonly IReadOnlyDictionary<int, string> FatalCloseCodes =
        new Dictionary<int, string>
        {
            [4004] = "Discord rejected the bot token (authentication failed). "
                   + "Check the Discord:BotToken secret.",
            [4010] = "Discord rejected the connection: invalid shard.",
            [4011] = "Discord requires sharding for this bot, which Netclaw does not support.",
            [4012] = "Discord rejected the connection: invalid API version.",
            [4013] = "Discord rejected the connection: invalid gateway intent(s).",
            [4014] = "Discord rejected the connection: disallowed gateway intent(s). "
                   + "Enable the Message Content intent under Privileged Gateway Intents "
                   + "in the Discord Developer Portal, then restart the daemon.",
        };

    /// <summary>
    /// Classifies <paramref name="failure"/>. Idempotent — an already-classified
    /// <see cref="ChannelConnectException"/> is returned unchanged.
    /// </summary>
    public static ChannelConnectException Classify(Exception failure)
    {
        if (failure is ChannelConnectException already)
            return already;

        if (FindInner<WebSocketClosedException>(failure) is { } closed
            && FatalCloseCodes.TryGetValue(closed.CloseCode, out var closeReason))
        {
            return new ChannelConnectException(ChannelConnectFailureKind.Fatal, closeReason, failure);
        }

        if (FindInner<HttpException>(failure) is { } http
            && http.HttpCode == HttpStatusCode.Unauthorized)
        {
            return new ChannelConnectException(
                ChannelConnectFailureKind.Fatal,
                "Discord rejected the bot token (HTTP 401 Unauthorized). "
                + "Check the Discord:BotToken secret.",
                failure);
        }

        return new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            $"Discord gateway connection failed: {failure.Message}",
            failure);
    }

    private static T? FindInner<T>(Exception? ex)
        where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is T match)
                return match;
        }

        return null;
    }
}
