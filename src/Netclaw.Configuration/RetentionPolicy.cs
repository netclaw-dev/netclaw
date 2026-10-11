// -----------------------------------------------------------------------
// <copyright file="RetentionPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>
/// The rules every <c>Retention:*:Days</c> setting shares: how the value is read and how a
/// number of days becomes a cutoff date. A retention job calls these so each setting behaves the
/// same way: a missing value takes the default, a value that is not an integer takes the default
/// with a warning (the daemon must still start), and zero keeps everything.
/// </summary>
internal static class RetentionPolicy
{
    /// <summary>The longest setting <see cref="TryParseDays"/> accepts; the JSON schema has the same maximum.</summary>
    public const int MaxDays = 36500;

    /// <summary>
    /// Checks a number of days typed by a person (the config editor and the command line). The
    /// daemon is more lenient on purpose, because it must still start with a hand-edited value;
    /// see <see cref="ResolveDays"/>.
    /// </summary>
    public static bool TryParseDays(string? text, out int days, out string error)
    {
        error = $"Days must be a whole number from 0 to {MaxDays} (0 keeps the data forever).";
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out days) && days is >= 0 and <= MaxDays)
        {
            error = string.Empty;
            return true;
        }

        days = 0;
        return false;
    }

    /// <summary>
    /// Reads <paramref name="key"/> as a number of days. Returns <paramref name="defaultDays"/>
    /// when the key is missing or blank, and also, with a <paramref name="warning"/> for the caller
    /// to surface, when the value is not an integer.
    /// </summary>
    public static int ResolveDays(IConfiguration configuration, string key, int defaultDays, out string? warning)
    {
        warning = null;
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return defaultDays;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            return days;

        warning = $"{key} value '{raw}' is not an integer; using the default of {defaultDays} days.";
        return defaultDays;
    }

    /// <summary>
    /// The first UTC date that is still kept: items dated before it are expired. Returns false
    /// when nothing can expire: zero or a negative number of days means keep forever, and so does
    /// a number of days longer than the calendar itself (which would make the subtraction throw).
    /// </summary>
    public static bool TryGetCutoff(DateTimeOffset now, int days, out DateOnly cutoff)
    {
        cutoff = default;
        if (days <= 0)
            return false;

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (days > today.DayNumber)
            return false;

        cutoff = today.AddDays(-days);
        return true;
    }
}
