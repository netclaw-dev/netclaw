using Cronos;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Wraps Cronos for cron expression parsing and next-occurrence computation.
/// Uses <see cref="TimeProvider"/> for testable time.
/// </summary>
public static class CronScheduleHelper
{
    /// <summary>
    /// Computes the next occurrence of the given cron expression after <paramref name="from"/>.
    /// Returns null if the expression has no future occurrence.
    /// </summary>
    public static DateTimeOffset? GetNextOccurrence(string cronExpression, DateTimeOffset from)
    {
        var expression = CronExpression.Parse(cronExpression, CronFormat.Standard);
        return expression.GetNextOccurrence(from, TimeZoneInfo.Utc);
    }

    /// <summary>
    /// Computes the next occurrence using the given <see cref="TimeProvider"/> for current time.
    /// </summary>
    public static DateTimeOffset? GetNextOccurrence(string cronExpression, TimeProvider timeProvider)
    {
        return GetNextOccurrence(cronExpression, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Validates whether the given string is a valid 5-field cron expression.
    /// </summary>
    public static bool TryParse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        try
        {
            CronExpression.Parse(expression, CronFormat.Standard);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Translates a 5-field cron expression to a human-readable English description.
    /// Covers common patterns; falls back to the raw expression for complex cases.
    /// </summary>
    public static string Describe(string cronExpression)
    {
        var parts = cronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
            var timeStr = $"{h:D2}:{m:D2} UTC";

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
