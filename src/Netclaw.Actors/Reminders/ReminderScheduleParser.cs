using System.Globalization;
using System.Text.RegularExpressions;

namespace Netclaw.Actors.Reminders;

public static partial class ReminderScheduleParser
{
    /// <summary>
    /// Minimum allowed interval for recurring reminders. Guardrail against
    /// accidental tight loops — not operator-configurable.
    /// </summary>
    internal const int MinIntervalSeconds = 60;

    [GeneratedRegex(
        "^(?:every\\s+)?(\\d+)\\s*(s|sec|secs|seconds?|m|min|mins|minutes?|h|hr|hrs|hours?|d|days?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RelativeTimePattern();

    public static (ReminderSchedule? Schedule, string? Error) Parse(
        string scheduleType,
        string scheduleValue,
        TimeProvider timeProvider)
    {
        var type = scheduleType.ToLowerInvariant() switch
        {
            "once" or "one-shot" or "oneshot" => ReminderScheduleType.OneShot,
            "interval" or "recurring" or "repeat" => ReminderScheduleType.Interval,
            "cron" => ReminderScheduleType.Cron,
            _ => (ReminderScheduleType?)null
        };

        if (type is null)
            return (null, $"Unknown schedule type '{scheduleType}'. Use 'once', 'interval', or 'cron'.");

        switch (type.Value)
        {
            case ReminderScheduleType.OneShot:
            {
                var fireAt = ParseTimeValue(scheduleValue, timeProvider);
                if (fireAt is null)
                {
                    return (null,
                        $"Cannot parse schedule '{scheduleValue}'. Use relative time (e.g. '30m', '2h') or ISO 8601 datetime.");
                }

                return (new ReminderSchedule
                {
                    Type = ReminderScheduleType.OneShot,
                    FireAt = fireAt,
                    OriginalExpression = scheduleValue
                }, null);
            }

            case ReminderScheduleType.Interval:
            {
                var interval = ParseDuration(scheduleValue);
                if (interval is null)
                    return (null, $"Cannot parse interval '{scheduleValue}'. Use format like '30m', '2h', '1d'.");
                if (interval.Value.TotalSeconds < MinIntervalSeconds)
                    return (null, $"Minimum interval is {MinIntervalSeconds} seconds.");

                return (new ReminderSchedule
                {
                    Type = ReminderScheduleType.Interval,
                    Interval = interval,
                    FireAt = timeProvider.GetUtcNow().Add(interval.Value),
                    OriginalExpression = scheduleValue
                }, null);
            }

            case ReminderScheduleType.Cron:
            {
                if (!CronScheduleHelper.TryParse(scheduleValue))
                {
                    return (null,
                        $"Invalid cron expression '{scheduleValue}'. Use standard 5-field format (minute hour day month weekday).");
                }

                return (new ReminderSchedule
                {
                    Type = ReminderScheduleType.Cron,
                    CronExpression = scheduleValue,
                    OriginalExpression = scheduleValue
                }, null);
            }

            default:
                return (null, "Unknown schedule type.");
        }
    }

    private static DateTimeOffset? ParseTimeValue(string value, TimeProvider timeProvider)
    {
        var duration = ParseDuration(value);
        if (duration is not null)
            return timeProvider.GetUtcNow().Add(duration.Value);

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        return null;
    }

    private static TimeSpan? ParseDuration(string value)
    {
        var match = RelativeTimePattern().Match(value.Trim());
        if (!match.Success)
            return null;

        var amount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Value.ToLowerInvariant();

        return unit switch
        {
            "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(amount),
            "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(amount),
            "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(amount),
            "d" or "day" or "days" => TimeSpan.FromDays(amount),
            _ => null
        };
    }
}
