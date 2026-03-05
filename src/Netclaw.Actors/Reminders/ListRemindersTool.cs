using System.ComponentModel;
using System.Text;
using Akka.Actor;
using Netclaw.Tools;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// LLM tool for listing all active reminders.
/// </summary>
[NetclawTool("list_reminders",
    "List all scheduled reminders with their IDs, names, schedules, and next fire times.",
    Grant = "scheduling")]
public sealed partial class ListRemindersTool : NetclawTool<ListRemindersTool.Params>
{
    private readonly IActorRef _reminderManager;

    public record Params(
        [property: Description("Optional filter: 'active' (default) or 'all'")]
        string? Filter = null);

    public ListRemindersTool(IActorRef reminderManager)
    {
        _reminderManager = reminderManager;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var response = await _reminderManager.Ask<ReminderListResponse>(
            new ListRemindersCommand(), TimeSpan.FromSeconds(10), ct);

        if (response.Reminders.Count == 0)
            return "No active reminders.";

        var sb = new StringBuilder();
        sb.AppendLine($"Active reminders ({response.Reminders.Count}):");
        sb.AppendLine();

        foreach (var r in response.Reminders)
        {
            var scheduleDesc = r.Schedule.Type switch
            {
                ReminderScheduleType.OneShot => $"once at {r.NextFire:u}",
                ReminderScheduleType.Interval => $"every {r.Schedule.Interval!.Value.TotalMinutes:F0}m",
                ReminderScheduleType.Cron => $"cron '{r.Schedule.CronExpression}'",
                _ => r.Schedule.OriginalExpression ?? "unknown"
            };

            sb.AppendLine($"  ID: {r.Id.Value}");
            sb.AppendLine($"  Name: {r.Name}");
            sb.AppendLine($"  Schedule: {scheduleDesc}");
            if (r.NextFire is not null)
                sb.AppendLine($"  Next fire: {r.NextFire:u}");
            if (r.ReportToChannel is not null)
                sb.AppendLine($"  Report to: {r.ReportToChannel}");
            sb.AppendLine($"  Prompt: {(r.Prompt.Length > 100 ? r.Prompt[..100] + "..." : r.Prompt)}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
