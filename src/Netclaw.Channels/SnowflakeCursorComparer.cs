// -----------------------------------------------------------------------
// <copyright file="SnowflakeCursorComparer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels;

/// <summary>
/// Orders Discord snowflake cursors that are held as strings. A shorter
/// digit string is always the smaller number, so the comparison is length
/// first and then ordinal. Plain ordinal comparison is wrong across a
/// digit-length boundary: it puts the 18-digit "999999999999999999" after
/// the 19-digit "1000000000000000000".
/// </summary>
/// <remarks>
/// The comparer expects a canonical unsigned decimal string: digits only,
/// no sign, no whitespace, and no leading zero. Discord snowflakes have
/// that form, and the Discord binding actor normalizes every cursor value
/// through <see cref="ulong"/> before it stores or persists the value. A
/// non-canonical input has no defined order here.
/// </remarks>
public sealed class SnowflakeCursorComparer : IComparer<string>
{
    public static readonly SnowflakeCursorComparer Instance = new();

    private SnowflakeCursorComparer()
    {
    }

    /// <summary>
    /// Compares two snowflake cursors. A null value is smaller than any
    /// non-null value, which matches the framework comparer convention and
    /// the "no cursor yet" state of a fresh binding.
    /// </summary>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        if (x.Length != y.Length)
            return x.Length < y.Length ? -1 : 1;

        return Math.Sign(string.CompareOrdinal(x, y));
    }
}
