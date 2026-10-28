// -----------------------------------------------------------------------
// <copyright file="DaemonLogFileName.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.RegularExpressions;

namespace Netclaw.Configuration;

/// <summary>Parses the daemon date and optional numeric segment from an exact file name.</summary>
public static class DaemonLogFileName
{
    private static readonly Regex Pattern = new(
        "\\Adaemon-([0-9]{4}-[0-9]{2}-[0-9]{2})(?:\\.([1-9][0-9]{0,9}))?\\.log\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Accepts an exact date and a positive ASCII Int32 segment without initial zeros.</summary>
    public static bool TryParse(string fileName, out DateOnly date, out int segment)
    {
        date = default;
        segment = 0;
        var match = Pattern.Match(fileName);
        return match.Success
            && (!match.Groups[2].Success
                || int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out segment))
            && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
