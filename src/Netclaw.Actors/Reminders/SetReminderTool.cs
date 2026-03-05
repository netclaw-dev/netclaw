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
    "Schedule a reminder that will execute at a specific time or on a recurring schedule. " +
    "Supports relative durations ('30m', '2h'), interval schedules ('every 6h'), and cron ('0 */6 * * *').",
    Grant = "scheduling")]
public sealed partial class SetReminderTool : NetclawTool<SetReminderTool.Params>
{
    private readonly IActorRef _reminderManager;
    private readonly TimeProvider _timeProvider;
    private readonly ReminderConfig _config;

    public record Params(
        [property: Description("A short title for this reminder (e.g. 'daily-standup').")]
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
        if (string.IsNullOrWhiteSpace(args.Name))
            return "Error: 'name' is required.";
        if (string.IsNullOrWhiteSpace(args.Prompt))
            return "Error: 'prompt' is required.";
        if (string.IsNullOrWhiteSpace(args.Schedule))
            return "Error: 'schedule' is required.";

        var (schedule, error) = ReminderScheduleParser.Parse(
            args.ScheduleType,
            args.Schedule,
            _timeProvider,
            _config);

        if (schedule is null)
            return $"Error: {error}";

        var id = ReminderIdGenerator.Generate(args.Name);
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
            new SaveReminderCommand(definition, ReminderWriteMode.CreateOnly), TimeSpan.FromSeconds(10), ct);

        if (!response.Success)
            return $"Failed to schedule reminder '{args.Name}': {response.ErrorMessage ?? "unknown error"}";

        var scheduleDesc = schedule.Type switch
        {
            ReminderScheduleType.OneShot => $"once at {response.NextFire:u}",
            ReminderScheduleType.Interval => $"every {schedule.Interval!.Value.TotalMinutes:F0}m, next: {response.NextFire:u}",
            ReminderScheduleType.Cron => $"cron '{schedule.CronExpression}', next: {response.NextFire:u}",
            _ => response.NextFire?.ToString("u") ?? "scheduled"
        };

        return $"Reminder '{args.Name}' scheduled ({scheduleDesc}). ID: {id.Value}";
    }
}
