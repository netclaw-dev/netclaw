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
}
