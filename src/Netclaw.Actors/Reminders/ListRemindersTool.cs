// -----------------------------------------------------------------------
// <copyright file="ListRemindersTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Akka.Actor;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// LLM tool for listing reminder definitions.
/// </summary>
[NetclawTool("list_reminders",
    "List reminder definitions with IDs, schedules, status, and next fire times.",
    Grant = "scheduling")]
public sealed partial class ListRemindersTool : NetclawTool<ListRemindersTool.Params>
{
    private readonly IActorRef _reminderManager;
    private readonly SchedulingConfig _schedulingConfig;

    public record Params(
        [property: Description("Optional filter: 'active' (default) or 'all'.")]
        string? Filter = null);

    public ListRemindersTool(IActorRef reminderManager, SchedulingConfig schedulingConfig)
    {
        _reminderManager = reminderManager;
        _schedulingConfig = schedulingConfig;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (!_schedulingConfig.Enabled)
            return "Error: Scheduling is disabled for this deployment.";

        var includeDisabled = string.Equals(args.Filter, "all", StringComparison.OrdinalIgnoreCase);

        var response = await _reminderManager.Ask<ReminderListResponse>(
            new ListRemindersCommand(includeDisabled), TimeSpan.FromSeconds(10), ct);

        if (response.Reminders.Count == 0)
            return includeDisabled ? "No reminders found." : "No active reminders.";

        var sb = new StringBuilder();
        sb.AppendLine($"Reminders ({response.Reminders.Count}):");
        sb.AppendLine();

        foreach (var r in response.Reminders)
        {
            var scheduleDesc = DescribeSchedule(r.Schedule);

            sb.AppendLine($"  ID: {r.Id.Value}");
            sb.AppendLine($"  Title: {r.Title}");
            sb.AppendLine($"  Status: {(r.Enabled ? "active" : "disabled")}");
            sb.AppendLine($"  Schedule: {scheduleDesc}");
            if (r.NextFire is not null)
                sb.AppendLine($"  Next fire: {SetReminderTool.FormatTimestamp(r.NextFire)}");
            if (r.ExpiresAt is not null)
                sb.AppendLine($"  Expires: {SetReminderTool.FormatTimestamp(r.ExpiresAt)}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public static string DescribeSchedule(ReminderSchedule schedule) => schedule.Type switch
    {
        ReminderScheduleType.OneShot => "runs once",
        ReminderScheduleType.Interval when schedule.Interval is { } iv => $"runs {FormatInterval(iv)}",
        ReminderScheduleType.Cron when schedule.CronExpression is not null =>
            $"runs {CronScheduleHelper.Describe(schedule.CronExpression)}",
        _ => schedule.OriginalExpression ?? "unknown"
    };

    private static string FormatInterval(TimeSpan interval) => interval.TotalHours switch
    {
        >= 24 when interval.TotalHours % 24 == 0 => $"every {interval.TotalDays:F0} day(s)",
        >= 1 when interval.TotalMinutes % 60 == 0 => $"every {interval.TotalHours:F0}h",
        _ => $"every {interval.TotalMinutes:F0}m"
    };
}
