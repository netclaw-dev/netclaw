using System.ComponentModel;
using Akka.Actor;
using Netclaw.Tools;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// LLM tool for deleting a reminder by ID.
/// </summary>
[NetclawTool("cancel_reminder",
    "Delete a reminder by its ID. Use list_reminders to find reminder IDs.",
    Grant = "scheduling")]
public sealed partial class CancelReminderTool : NetclawTool<CancelReminderTool.Params>
{
    private readonly IActorRef _reminderManager;

    public record Params(
        [property: Description("The reminder ID to cancel (returned by set_reminder or list_reminders)")]
        string ReminderId);

    public CancelReminderTool(IActorRef reminderManager)
    {
        _reminderManager = reminderManager;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.ReminderId))
            return "Error: 'reminderId' is required.";

        var id = new ReminderId(args.ReminderId);
        var response = await _reminderManager.Ask<ReminderCancelledResponse>(
            new CancelReminderCommand(id), TimeSpan.FromSeconds(10), ct);

        return response.Found
            ? $"Reminder '{args.ReminderId}' deleted."
            : $"Reminder '{args.ReminderId}' not found.";
    }
}
