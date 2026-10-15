// -----------------------------------------------------------------------
// <copyright file="ReminderFireKey.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// The stable key of one reminder fire: the reminder id and the scheduled fire time, written as
/// <c>"{id}:{fireTimeMs}"</c>. A redelivery of a fire carries the same key. The key is also the
/// recorded source message id of a reminder input, so a session reads the reminder id from a stored input
/// without a journal change.
/// </summary>
public readonly record struct ReminderFireKey(ReminderId DefinitionId, long FireTimeMs)
{
    /// <summary>The wire text of the key. It is the text that old journal rows already hold.</summary>
    public string Value => string.Create(CultureInfo.InvariantCulture, $"{DefinitionId.Value}:{FireTimeMs}");

    /// <summary>
    /// Reads a fire key from its wire text. Text that is not a fire key gives <c>false</c>, so the caller can
    /// treat the owner of that text as unknown. A reminder id is a slug and has no colon. The time is digits only.
    /// </summary>
    public static bool TryParse(string? text, out ReminderFireKey key)
    {
        key = default;
        if (text is null)
            return false;

        var separator = text.LastIndexOf(':');
        if (separator <= 0 || text.IndexOf(':', StringComparison.Ordinal) != separator)
            return false;

        // NumberStyles.None rejects a sign, a space, and any non-digit.
        if (!long.TryParse(text.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var fireTimeMs))
            return false;

        key = new ReminderFireKey(new ReminderId(text[..separator]), fireTimeMs);
        return true;
    }

    public override string ToString() => Value;
}
