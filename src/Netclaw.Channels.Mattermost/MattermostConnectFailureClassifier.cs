// -----------------------------------------------------------------------
// <copyright file="MattermostConnectFailureClassifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Channels.Mattermost;

/// <summary>
/// Maps a Mattermost connection failure onto a <see cref="ChannelConnectException"/>
/// so the channel host can tell a fixable misconfiguration (bad/revoked token,
/// wrong server URL) apart from a transient network error. Mattermost surfaces
/// auth failures as HTTP 401 responses and error ids inside the exception
/// message, so classification is by substring match across the exception chain.
/// </summary>
internal static class MattermostConnectFailureClassifier
{
    // Mattermost signals that indicate a configuration/permission problem an
    // operator must fix — retrying would loop forever.
    private static readonly (string Token, string Reason)[] FatalSignals =
    {
        ("401", "Mattermost rejected the bot token (HTTP 401). Check the Mattermost:BotToken secret."),
        ("unauthorized", "Mattermost rejected the request as unauthorized. Check the Mattermost:BotToken secret."),
        ("invalid_token", "The Mattermost token is invalid (invalid_token). Re-issue it and update the Mattermost:BotToken secret."),
        ("session_expired", "The Mattermost session has expired (session_expired). Re-issue the bot token."),
        ("invalid or expired", "The Mattermost token is invalid or expired. Re-issue it and update the Mattermost:BotToken secret."),
        ("invalid session", "Mattermost rejected the session. Re-issue the bot token."),
        ("403", "Mattermost rejected the request as forbidden (HTTP 403). The bot account may lack required permissions."),
        ("no such host", "The Mattermost server URL could not be resolved. Check the Mattermost:ServerUrl setting."),
        ("name or service not known", "The Mattermost server URL could not be resolved. Check the Mattermost:ServerUrl setting."),
    };

    /// <summary>
    /// Classifies <paramref name="failure"/>. Idempotent — an already-classified
    /// <see cref="ChannelConnectException"/> is returned unchanged.
    /// </summary>
    public static ChannelConnectException Classify(Exception failure)
    {
        if (failure is ChannelConnectException already)
            return already;

        var text = CollectMessages(failure);
        foreach (var (token, reason) in FatalSignals)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                return new ChannelConnectException(ChannelConnectFailureKind.Fatal, reason, failure);
        }

        return new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            $"Mattermost connection failed: {failure.Message}",
            failure);
    }

    private static string CollectMessages(Exception? ex)
    {
        var builder = new StringBuilder();
        for (; ex is not null; ex = ex.InnerException)
            builder.Append(ex.Message).Append('\n');

        return builder.ToString();
    }
}
