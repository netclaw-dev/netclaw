// -----------------------------------------------------------------------
// <copyright file="CronScheduleHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Cronos;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Wraps Cronos for cron expression parsing and next-occurrence computation.
/// Uses <see cref="TimeProvider"/> for testable time.
/// Supports an optional leading <c>CRON_TZ=&lt;time-zone-id&gt;</c> prefix (Vixie crontab syntax)
/// to evaluate the schedule in a specific time zone; without it, schedules are evaluated in UTC.
/// </summary>
/// <remarks>
/// Time zone identifiers in the <c>CRON_TZ</c> prefix must be IANA identifiers without spaces
/// (e.g. <c>Europe/Brussels</c>, <c>America/New_York</c>). Windows display names such as
/// <c>Eastern Standard Time</c> are not supported: the zone id ends at the first space, so
/// multi-word names would resolve to a truncated, unknown identifier. Use IANA names instead.
/// </remarks>
public static class CronScheduleHelper
{
    private const string CronTzPrefix = "CRON_TZ=";

    /// <summary>
    /// Computes the next occurrence of the given cron expression after <paramref name="from"/>.
    /// Returns null if the expression has no future occurrence.
    /// Throws <see cref="CronFormatException"/> if the expression is invalid or references an unknown time zone.
    /// </summary>
    public static DateTimeOffset? GetNextOccurrence(string cronExpression, DateTimeOffset from)
    {
        var (fields, timeZone) = SplitTimeZone(cronExpression);
        var expression = CronExpression.Parse(fields, CronFormat.Standard);
        return expression.GetNextOccurrence(from, timeZone);
    }

    /// <summary>
    /// Computes the next occurrence using the given <see cref="TimeProvider"/> for current time.
    /// </summary>
    public static DateTimeOffset? GetNextOccurrence(string cronExpression, TimeProvider timeProvider)
    {
        return GetNextOccurrence(cronExpression, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Validates whether the given string is a valid 5-field cron expression,
    /// optionally preceded by a <c>CRON_TZ=&lt;time-zone-id&gt;</c> prefix.
    /// </summary>
    public static bool TryParse(string expression)
    {
        return TryParse(expression, out _);
    }

    /// <summary>
    /// Validates whether the given string is a valid 5-field cron expression,
    /// optionally preceded by a <c>CRON_TZ=&lt;time-zone-id&gt;</c> prefix.
    /// When valid, <paramref name="timeZone"/> receives the resolved zone (UTC when no prefix is present).
    /// </summary>
    public static bool TryParse(string expression, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;

        if (string.IsNullOrWhiteSpace(expression))
            return false;

        TimeZoneInfo parsedZone;
        try
        {
            (_, parsedZone) = SplitTimeZone(expression);
        }
        catch (CronFormatException)
        {
            return false;
        }

        try
        {
            CronExpression.Parse(StripTimeZone(expression), CronFormat.Standard);
        }
        catch (CronFormatException)
        {
            return false;
        }

        timeZone = parsedZone;
        return true;
    }

    /// <summary>
    /// Splits an optional <c>CRON_TZ=&lt;time-zone-id&gt;</c> prefix from the expression and
    /// resolves the zone. Returns <see cref="TimeZoneInfo.Utc"/> when no prefix is present.
    /// Throws <see cref="CronFormatException"/> when the prefix references an unknown time zone.
    /// The zone id must be an IANA identifier without spaces (e.g. <c>Europe/Brussels</c>);
    /// it ends at the first space, so multi-word Windows names like <c>Eastern Standard Time</c>
    /// are not supported.
    /// </summary>
    internal static (string Fields, TimeZoneInfo TimeZone) SplitTimeZone(string cronExpression)
    {
        var trimmed = cronExpression.Trim();
        if (!trimmed.StartsWith(CronTzPrefix, StringComparison.OrdinalIgnoreCase))
            return (trimmed, TimeZoneInfo.Utc);

        var rest = trimmed[CronTzPrefix.Length..];
        var spaceIndex = rest.IndexOf(' ', StringComparison.Ordinal);
        if (spaceIndex <= 0)
            throw new CronFormatException(
                "CRON_TZ prefix must be followed by a space and a 5-field cron expression.");

        var zoneId = rest[..spaceIndex].Trim();
        if (zoneId.Length == 0)
            throw new CronFormatException(
                "CRON_TZ prefix requires a time zone identifier. Use an IANA time zone id without spaces (e.g. 'Europe/Brussels').");

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            return (rest[spaceIndex..].Trim(), zone);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new CronFormatException(
                $"Unknown time zone '{zoneId}' in CRON_TZ prefix. Use an IANA time zone id without spaces (e.g. 'Europe/Brussels').");
        }
        catch (InvalidTimeZoneException)
        {
            throw new CronFormatException(
                $"Invalid time zone '{zoneId}' in CRON_TZ prefix. Use an IANA time zone id without spaces (e.g. 'Europe/Brussels').");
        }
    }

    private static string StripTimeZone(string cronExpression) => SplitTimeZone(cronExpression).Fields;

    /// <summary>
    /// Translates a 5-field cron expression to a human-readable English description.
    /// Covers common patterns; falls back to the raw expression for complex cases.
    /// Handles an optional leading <c>CRON_TZ=&lt;time-zone-id&gt;</c> prefix and reports the zone.
    /// </summary>
    public static string Describe(string cronExpression)
    {
        string fields;
        string zoneLabel;
        try
        {
            (fields, var zone) = SplitTimeZone(cronExpression);
            zoneLabel = zone == TimeZoneInfo.Utc ? "UTC" : zone.Id;
        }
        catch (CronFormatException)
        {
            return cronExpression;
        }

        var parts = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return cronExpression;

        var (minute, hour, dom, month, dow) = (parts[0], parts[1], parts[2], parts[3], parts[4]);

        // Every N minutes: */15 * * * *
        if (minute.StartsWith("*/", StringComparison.Ordinal) && hour == "*" && dom == "*" && month == "*" && dow == "*"
            && int.TryParse(minute[2..], out var everyMin))
            return $"every {everyMin} minute(s)";

        // Every N hours: 0 */6 * * *
        if (minute == "0" && hour.StartsWith("*/", StringComparison.Ordinal) && dom == "*" && month == "*" && dow == "*"
            && int.TryParse(hour[2..], out var everyHr))
            return $"every {everyHr} hour(s)";

        // Specific time patterns (minute and hour are fixed numbers)
        if (int.TryParse(minute, out var m) && int.TryParse(hour, out var h))
        {
            var timeStr = $"{h:D2}:{m:D2} {zoneLabel}";

            // Daily: 0 9 * * *
            if (dom == "*" && month == "*" && dow == "*")
                return $"daily at {timeStr}";

            // Specific days of week: 0 9 * * MON-FRI or 0 9 * * 1,3,5
            if (dom == "*" && month == "*" && dow != "*")
            {
                var dowDesc = DescribeDaysOfWeek(dow);
                return dowDesc is not null
                    ? $"{dowDesc} at {timeStr}"
                    : $"cron '{cronExpression}'";
            }

            // Specific day of month: 0 9 1 * *
            if (month == "*" && dow == "*" && int.TryParse(dom, out var d))
                return $"monthly on day {d} at {timeStr}";
        }

        return $"cron '{cronExpression}'";
    }

    private static string? DescribeDaysOfWeek(string dow)
    {
        return dow.ToUpperInvariant() switch
        {
            "MON-FRI" or "1-5" => "weekdays",
            "SAT,SUN" or "0,6" or "6,0" => "weekends",
            "MON" or "1" => "every Monday",
            "TUE" or "2" => "every Tuesday",
            "WED" or "3" => "every Wednesday",
            "THU" or "4" => "every Thursday",
            "FRI" or "5" => "every Friday",
            "SAT" or "6" => "every Saturday",
            "SUN" or "0" or "7" => "every Sunday",
            _ => TryDescribeDayList(dow)
        };
    }

    private static string? TryDescribeDayList(string dow)
    {
        var dayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = "Sun", ["1"] = "Mon", ["2"] = "Tue", ["3"] = "Wed",
            ["4"] = "Thu", ["5"] = "Fri", ["6"] = "Sat", ["7"] = "Sun",
            ["MON"] = "Mon", ["TUE"] = "Tue", ["WED"] = "Wed", ["THU"] = "Thu",
            ["FRI"] = "Fri", ["SAT"] = "Sat", ["SUN"] = "Sun"
        };

        var tokens = dow.Split(',');
        if (tokens.Length < 2)
            return null;

        var names = new List<string>();
        foreach (var token in tokens)
        {
            if (!dayNames.TryGetValue(token.Trim(), out var name))
                return null;
            names.Add(name);
        }

        return $"every {string.Join(", ", names)}";
    }
}
