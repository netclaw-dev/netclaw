using System.ComponentModel;
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// LLM tool for scheduling reminders. Supports one-shot, interval, and cron schedules.
/// </summary>
[NetclawTool("set_reminder",
    "Schedule or update a reminder by ID. If a reminder with the given ID already exists it will be updated (upsert). " +
    "Supports relative durations ('30m', '2h'), interval schedules ('every 6h'), and cron ('0 */6 * * *').",
    Grant = "scheduling")]
public sealed partial class SetReminderTool : NetclawTool<SetReminderTool.Params>
{
    private readonly IActorRef _reminderManager;
    private readonly TimeProvider _timeProvider;
    private readonly ReminderConfig _config;

    public record Params(
        [property: Description("Stable identifier for this reminder (kebab-case slug, e.g. 'daily-standup'). If a reminder with this ID exists it will be updated.")]
        string Id,
        [property: Description("A short human-readable title for this reminder.")]
        string Name,
        [property: Description("Execution instructions for this reminder.")]
        string Prompt,
        [property: Description("Schedule type: 'once', 'interval', or 'cron'.")]
        string ScheduleType,
        [property: Description("Schedule value: relative time, ISO 8601 datetime, interval duration, or cron expression.")]
        string Schedule,
        [property: Description("Optional Slack channel ID for reporting. Omit for current-session targeting.")]
        string? ReportToChannel = null,
        [property: Description("Optional notify instructions describing how Netclaw should report results.")]
        string? NotifyInstructions = null);

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
        if (string.IsNullOrWhiteSpace(args.Id))
            return "Error: 'id' is required.";
        if (string.IsNullOrWhiteSpace(args.Name))
            return "Error: 'name' is required.";
        if (string.IsNullOrWhiteSpace(args.Prompt))
            return "Error: 'prompt' is required.";
        if (string.IsNullOrWhiteSpace(args.Schedule))
            return "Error: 'schedule' is required.";

        // Normalize ID at the tool boundary: lowercase, kebab-case, length cap
        var normalizedId = ReminderIdGenerator.Normalize(args.Id);

        var (schedule, error) = ReminderScheduleParser.Parse(
            args.ScheduleType,
            args.Schedule,
            _timeProvider,
            _config);

        if (schedule is null)
            return $"Error: {error}";

        var id = new ReminderId(normalizedId);
        var now = _timeProvider.GetUtcNow();

        string? sessionId = null;
        string? reportToChannel = args.ReportToChannel;
        string? reportToThreadTs = null;

        if (context.SessionId is not null && string.IsNullOrEmpty(reportToChannel))
        {
            sessionId = context.SessionId;

            var parts = context.SessionId.Split('/');
            if (parts.Length >= 2)
            {
                reportToChannel = parts[0];
                reportToThreadTs = parts[1];
            }
        }

        var notifyInstructions = args.NotifyInstructions;
        if (string.IsNullOrWhiteSpace(notifyInstructions))
        {
            notifyInstructions = reportToChannel is null
                ? "Reply in the originating session thread with a concise result."
                : $"Post the result to Slack channel {reportToChannel}.";
        }

        var definition = new ReminderDefinition
        {
            Id = id.Value,
            Title = args.Name,
            Schedule = schedule,
            Instructions = args.Prompt,
            NotifyInstructions = notifyInstructions,
            Enabled = true,
            SessionId = sessionId,
            ReportToChannel = reportToChannel,
            ReportToThreadTs = reportToThreadTs,
            CreatedBy = "llm-tool",
            CreatedAt = now,
            UpdatedAt = now
        };

        var response = await _reminderManager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition, ReminderWriteMode.Upsert), TimeSpan.FromSeconds(10), ct);

        if (!response.Success)
            return $"Failed to schedule reminder '{args.Name}': {response.ErrorMessage ?? "unknown error"}";

        var nextFireStr = FormatNextFire(response.NextFire);

        var scheduleDesc = schedule.Type switch
        {
            ReminderScheduleType.OneShot => $"Runs once. Next execution: {nextFireStr}.",
            ReminderScheduleType.Interval => $"Runs every {FormatInterval(schedule.Interval!.Value)}. Next execution: {nextFireStr}.",
            ReminderScheduleType.Cron => $"Runs {CronScheduleHelper.Describe(schedule.CronExpression!)}. Next execution: {nextFireStr}.",
            _ => $"Next execution: {nextFireStr}."
        };

        return $"Reminder '{args.Name}' scheduled. {scheduleDesc} ID: {id.Value}";
    }

    private static string FormatInterval(TimeSpan interval) => interval.TotalHours switch
    {
        >= 24 when interval.TotalHours % 24 == 0 => $"{interval.TotalDays:F0} day(s)",
        >= 1 when interval.TotalMinutes % 60 == 0 => $"{interval.TotalHours:F0}h",
        _ => $"{interval.TotalMinutes:F0}m"
    };

    public static string FormatNextFire(DateTimeOffset? nextFire)
    {
        if (nextFire is not { } nf)
            return "unknown";

        var local = nf.ToLocalTime();
        var tz = TimeZoneInfo.Local;
        var tzAbbrev = tz.IsDaylightSavingTime(local) ? tz.DaylightName : tz.StandardName;

        return $"{local:dddd, MMMM d 'at' h:mm tt} {tzAbbrev} ({nf:u})";
    }
}
