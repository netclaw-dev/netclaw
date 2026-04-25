using System.ComponentModel;
using Akka.Actor;
using Netclaw.Tools;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// LLM tool for marking a reminder as completed (disabled but preserved on disk).
/// </summary>
[NetclawTool("complete_reminder",
    "Mark a recurring reminder as completed — stops future executions while preserving " +
    "its definition and execution history. Use when the reminder's purpose is permanently " +
    "fulfilled (e.g., PR merged, deploy completed, issue resolved). " +
    "Use cancel_reminder instead to permanently delete a reminder and its history.",
    Grant = "scheduling")]
public sealed partial class CompleteReminderTool : NetclawTool<CompleteReminderTool.Params>
{
    private readonly IActorRef _reminderManager;

    public record Params(
        [property: Description("The reminder ID to complete (returned by set_reminder or list_reminders)")]
        string ReminderId);

    public CompleteReminderTool(IActorRef reminderManager)
    {
        _reminderManager = reminderManager;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.ReminderId))
            return "Error: 'reminderId' is required.";

        var id = new ReminderId(args.ReminderId);
        var response = await _reminderManager.Ask<ReminderStateResponse>(
            new DisableReminderCommand(id), TimeSpan.FromSeconds(10), ct);

        if (!response.Found)
            return $"Reminder '{args.ReminderId}' not found.";

        return response.Enabled
            ? $"Error: Failed to complete reminder '{args.ReminderId}'."
            : $"Reminder '{args.ReminderId}' marked as completed and disabled.";
    }
}
