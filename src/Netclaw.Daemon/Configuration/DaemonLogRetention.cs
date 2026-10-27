// -----------------------------------------------------------------------
// <copyright file="DaemonLogRetention.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Age-based pruning of the daemon's own rolled files in the top level of the logs directory.
/// Candidates use <c>daemon-yyyy-MM-dd.log</c>, <c>daemon-yyyy-MM-dd.N.log</c>, or a crash log name
/// that <see cref="CrashLogWriter.TryParseFileName"/> accepts. N is a positive integer without leading zeros.
/// Age comes from the date in the file name. Other files and subdirectories such as <c>sessions/</c> remain untouched.
/// </summary>
internal static class DaemonLogRetention
{
    /// <summary>
    /// The newest daemon logs and the newest crash logs are always kept, so a wrong clock cannot
    /// wipe the whole history.
    /// </summary>
    internal const int AlwaysKeepNewestDaemonLogs = 3;

    /// <inheritdoc cref="AlwaysKeepNewestDaemonLogs"/>
    internal const int AlwaysKeepNewestCrashLogs = 3;

    // No Netclaw build predates this; it keeps a decoy like daemon-0001-01-01.log out of reach.
    private const int EarliestPlausibleYear = 2000;

    private static readonly Regex DaemonFileNamePattern = new(
        "\\Adaemon-([0-9]{4}-[0-9]{2}-[0-9]{2})(?:\\.([1-9][0-9]{0,9}))?\\.log\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>The retention job for the daemon and crash logs in <paramref name="paths"/>.</summary>
    public static RetentionJob CreateJob(IConfiguration configuration, NetclawPaths paths, out string? warning)
    {
        var setting = RetentionSettings.Logs;
        var days = RetentionPolicy.ResolveDays(configuration, setting.ConfigKey, setting.DefaultDays, out warning);
        return new RetentionJob(
            "daemon and crash log",
            days,
            now => Prune(paths.LogsDirectory, now, days));
    }

    /// <summary>
    /// Deletes daemon and crash logs dated before <c>today - retentionDays</c> (UTC). Zero keeps
    /// everything (a negative value is treated the same, defensively). Today's file is never old
    /// enough to qualify, and the newest <see cref="AlwaysKeepNewestDaemonLogs"/> daemon logs stay
    /// whatever the clock says, as do the newest <see cref="AlwaysKeepNewestCrashLogs"/> crash logs. Returns the number deleted and the number that could not be
    /// (read-only volume, file held open elsewhere).
    /// </summary>
    public static (int Deleted, int Failed) Prune(string logsDirectory, DateTimeOffset now, int retentionDays)
    {
        if (!RetentionPolicy.TryGetCutoff(now, retentionDays, out var cutoff) || !Directory.Exists(logsDirectory))
            return (0, 0);

        string[] files;
        try
        {
            files = Directory.GetFiles(logsDirectory, "*.log", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 1);
        }

        var daemonLogs = new List<(string Path, DateOnly Date, int Segment)>();
        var crashLogs = new List<(string Path, DateTimeOffset Time)>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (TryGetDaemonLogDate(name, out var daemonDate, out var segment))
                daemonLogs.Add((file, daemonDate, segment));
            else if (CrashLogWriter.TryParseFileName(name, out var crashTime) && crashTime.Year >= EarliestPlausibleYear)
                crashLogs.Add((file, crashTime));
        }

        var candidates = crashLogs
            .OrderByDescending(static x => x.Time)
            .Skip(AlwaysKeepNewestCrashLogs)
            .Where(x => DateOnly.FromDateTime(x.Time.UtcDateTime) < cutoff)
            .Select(static x => x.Path)
            .ToList();

        candidates.AddRange(daemonLogs
            .OrderByDescending(static x => x.Date)
            .ThenByDescending(static x => x.Segment)
            .Skip(AlwaysKeepNewestDaemonLogs)
            // daemon-log-retention-cutoff:begin
            .Where(x => x.Date < cutoff)
            // daemon-log-retention-cutoff:end
            .Select(static x => x.Path));

        var deleted = 0;
        var failed = 0;
        foreach (var file in candidates)
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return (deleted, failed);
    }

    private static bool TryGetDaemonLogDate(string fileName, out DateOnly date, out int segment)
    {
        date = default;
        segment = 0;
        var match = DaemonFileNamePattern.Match(fileName);
        return match.Success
            && (!match.Groups[2].Success
                || int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out segment))
            && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            && date.Year >= EarliestPlausibleYear;
    }
}
