// -----------------------------------------------------------------------
// <copyright file="SlackConnectFailureClassifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Maps a Slack connection failure onto a <see cref="ChannelConnectException"/>
/// so the channel host can tell a fixable misconfiguration (bad/revoked token,
/// missing scope) apart from a transient network error. Slack surfaces these as
/// error-code strings inside <c>SlackException</c> messages, so classification
/// is by substring match across the exception chain.
/// </summary>
internal static class SlackConnectFailureClassifier
{
    // Slack API error codes that signal a configuration/permission problem an
    // operator must fix — retrying would loop forever.
    private static readonly (string Code, string Reason)[] FatalErrors =
    {
        ("invalid_auth", "Slack rejected the bot token (invalid_auth). Check the Slack:BotToken secret."),
        ("account_inactive", "The Slack workspace or bot account is inactive (account_inactive)."),
        ("token_revoked", "The Slack token has been revoked (token_revoked). Re-issue it and update the Slack:BotToken secret."),
        ("token_expired", "The Slack token has expired (token_expired). Re-issue it and update the Slack:BotToken secret."),
        ("not_authed", "No Slack token was supplied (not_authed)."),
        ("missing_scope", "The Slack token is missing a required OAuth scope (missing_scope)."),
        ("invalid_app_id", "The Slack app id is invalid (invalid_app_id)."),
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
        foreach (var (code, reason) in FatalErrors)
        {
            if (text.Contains(code, StringComparison.OrdinalIgnoreCase))
                return new ChannelConnectException(ChannelConnectFailureKind.Fatal, reason, failure);
        }

        return new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            $"Slack connection failed: {failure.Message}",
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
