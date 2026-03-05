using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// LLM tool for scheduling reminders. Supports one-shot, interval, and cron schedules.
/// </summary>
[NetclawTool("set_reminder",
    "Schedule a reminder that will execute a prompt at a specified time or on a recurring schedule. " +
    "Supports: relative time ('30m', '2h', '1d'), ISO 8601 datetime, interval ('every 6h'), " +
    "and cron expressions ('0 */6 * * *').",
    Grant = "scheduling")]
public sealed partial class SetReminderTool : NetclawTool<SetReminderTool.Params>
{
    private static readonly Regex RelativeTimePattern = new(
        @"^(?:every\s+)?(\d+)\s*(s|sec|secs|seconds?|m|min|mins|minutes?|h|hr|hrs|hours?|d|days?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IActorRef _reminderManager;
    private readonly TimeProvider _timeProvider;
    private readonly ReminderConfig _config;

    public record Params(
        [property: Description("A short name for this reminder (e.g. 'daily-standup', 'check-backups')")]
        string Name,
        [property: Description("The prompt to execute when the reminder fires")]
        string Prompt,
        [property: Description("Schedule type: 'once' for one-shot, 'interval' for recurring interval, 'cron' for cron expression")]
        string ScheduleType,
        [property: Description("Schedule value: relative time ('30m', '2h'), ISO 8601 datetime, interval duration ('6h'), or cron expression ('0 */6 * * *')")]
        string Schedule,
        [property: Description("Optional Slack channel ID to post results to. Omit for self-targeting (posts back to current thread).")]
        string? ReportToChannel = null);

    public SetReminderTool(IActorRef reminderManager, TimeProvider timeProvider, ReminderConfig config)
    {
        _reminderManager = reminderManager;
        _timeProvider = timeProvider;
        _config = config;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Name))
            return "Error: 'name' is required.";
        if (string.IsNullOrWhiteSpace(args.Prompt))
            return "Error: 'prompt' is required.";
        if (string.IsNullOrWhiteSpace(args.Schedule))
            return "Error: 'schedule' is required.";

        var (schedule, error) = ParseSchedule(args.ScheduleType, args.Schedule);
        if (schedule is null)
            return $"Error: {error}";

        var id = GenerateId(args.Name);
        var now = _timeProvider.GetUtcNow();

        // Determine self-targeting from context
        SessionId? originatingSession = null;
        string? reportToChannel = args.ReportToChannel;
        string? reportToThreadTs = null;

        if (context.SessionId is not null && string.IsNullOrEmpty(reportToChannel))
        {
            originatingSession = new SessionId(context.SessionId);
            // Extract channel/thread from session ID (format: channelId/threadTs)
            var parts = context.SessionId.Split('/');
            if (parts.Length >= 2)
            {
                reportToChannel = parts[0];
                reportToThreadTs = parts[1];
            }
        }

        var payload = new ReminderPayload
        {
            Id = id,
            Name = args.Name,
            Prompt = args.Prompt,
            Schedule = schedule,
            ReportToChannel = reportToChannel,
            ReportToThreadTs = reportToThreadTs,
            OriginatingSessionId = originatingSession,
            CreatedBy = "llm-tool",
            CreatedAt = now
        };

        var response = await _reminderManager.Ask<ReminderScheduledResponse>(
            new ScheduleReminderCommand(payload), TimeSpan.FromSeconds(10), ct);

        if (response.NextFire is null)
            return $"Failed to schedule reminder '{args.Name}'. The schedule may have no future occurrence.";

        var scheduleDesc = schedule.Type switch
        {
            ReminderScheduleType.OneShot => $"once at {response.NextFire:u}",
            ReminderScheduleType.Interval => $"every {schedule.Interval!.Value.TotalMinutes:F0}m, next: {response.NextFire:u}",
            ReminderScheduleType.Cron => $"cron '{schedule.CronExpression}', next: {response.NextFire:u}",
            _ => response.NextFire.Value.ToString("u")
        };

        return $"Reminder '{args.Name}' scheduled ({scheduleDesc}). ID: {id.Value}";
    }

    private (ReminderSchedule? schedule, string? error) ParseSchedule(string scheduleType, string scheduleValue)
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
                var fireAt = ParseTimeValue(scheduleValue);
                if (fireAt is null)
                    return (null, $"Cannot parse schedule '{scheduleValue}'. Use relative time (e.g. '30m', '2h') or ISO 8601 datetime.");
                return (new ReminderSchedule
                {
                    Type = ReminderScheduleType.OneShot,
                    FireAt = fireAt,
                    OriginalExpression = scheduleValue
                }, null);

            case ReminderScheduleType.Interval:
                var interval = ParseDuration(scheduleValue);
                if (interval is null)
                    return (null, $"Cannot parse interval '{scheduleValue}'. Use format like '30m', '2h', '1d'.");
                if (interval.Value.TotalSeconds < _config.MinIntervalSeconds)
                    return (null, $"Minimum interval is {_config.MinIntervalSeconds} seconds.");
                var firstFire = _timeProvider.GetUtcNow().Add(interval.Value);
                return (new ReminderSchedule
                {
                    Type = ReminderScheduleType.Interval,
                    Interval = interval,
                    FireAt = firstFire,
                    OriginalExpression = scheduleValue
                }, null);

            case ReminderScheduleType.Cron:
                if (!CronScheduleHelper.TryParse(scheduleValue))
                    return (null, $"Invalid cron expression '{scheduleValue}'. Use standard 5-field format (minute hour day month weekday).");
                return (new ReminderSchedule
                {
                    Type = ReminderScheduleType.Cron,
                    CronExpression = scheduleValue,
                    OriginalExpression = scheduleValue
                }, null);

            default:
                return (null, "Unknown schedule type.");
        }
    }

    private DateTimeOffset? ParseTimeValue(string value)
    {
        // Try relative time first
        var duration = ParseDuration(value);
        if (duration is not null)
            return _timeProvider.GetUtcNow().Add(duration.Value);

        // Try ISO 8601
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        return null;
    }

    private static TimeSpan? ParseDuration(string value)
    {
        var match = RelativeTimePattern.Match(value.Trim());
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

    private static ReminderId GenerateId(string name)
    {
        var slug = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('_', '-');
        // Keep only alphanumeric and hyphens
        slug = Regex.Replace(slug, @"[^a-z0-9\-]", "");
        if (slug.Length > 30)
            slug = slug[..30];
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return new ReminderId($"{slug}-{suffix}");
    }
}
