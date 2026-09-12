// -----------------------------------------------------------------------
// <copyright file="TelegramReminderTargetResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;

namespace Netclaw.Channels.Telegram;

/// <summary>
/// Resolves Telegram reminder targets from their signed numeric IDs.
/// Positive IDs identify users. Negative IDs identify groups or channels.
/// </summary>
public sealed class TelegramReminderTargetResolver(TelegramChannelOptions options) : IReminderTargetResolver
{
    public string Transport => "telegram";

    public Task<ReminderTargetResolution> ResolveAsync(string target, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target))
            return Task.FromResult(Failure("Target is required."));

        var canonicalId = target.Trim().TrimStart('@', '#');
        if (!long.TryParse(canonicalId, out var id) || id == 0)
        {
            return Task.FromResult(Failure(
                "Invalid Telegram target. Use a positive user ID or a negative group or channel ID."));
        }

        if (id > 0)
            return Task.FromResult(ResolveUser(canonicalId));

        return Task.FromResult(ResolveChat(canonicalId));
    }

    private ReminderTargetResolution ResolveUser(string userId)
    {
        if (!options.AllowDirectMessages)
            return Failure("Telegram direct messages are disabled in configuration.");

        return options.AllowedUserIds.Contains(userId, StringComparer.Ordinal)
            ? Success(userId, ReminderTargetKind.User)
            : Failure($"Telegram user '{userId}' is not in the allowed users list.");
    }

    private ReminderTargetResolution ResolveChat(string chatId) =>
        options.AllowedChatIds.Contains(chatId, StringComparer.Ordinal)
            ? Success(chatId, ReminderTargetKind.Channel)
            : Failure($"Telegram chat '{chatId}' is not in the allowed chats list.");

    private static ReminderTargetResolution Success(string id, ReminderTargetKind kind) =>
        new(true, id, kind, null);

    private static ReminderTargetResolution Failure(string error) =>
        new(false, null, ReminderTargetKind.Unknown, error);
}
