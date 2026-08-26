// -----------------------------------------------------------------------
// <copyright file="CancelReminderTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Akka.Actor;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// LLM tool for cancelling (disabling) a reminder by ID.
/// </summary>
[NetclawTool("cancel_reminder",
    "Cancel a reminder by its ID — disables it and stops future executions. The reminder definition is preserved for diagnosis. Use list_reminders to find reminder IDs.",
    Grant = "scheduling")]
public sealed partial class CancelReminderTool : NetclawTool<CancelReminderTool.Params>
{
    private readonly IActorRef _reminderManager;
    private readonly SchedulingConfig _schedulingConfig;

    public record Params(
        [property: Description("The reminder ID to cancel (returned by set_reminder or list_reminders)")]
        string ReminderId);

    public CancelReminderTool(IActorRef reminderManager, SchedulingConfig schedulingConfig)
    {
        _reminderManager = reminderManager;
        _schedulingConfig = schedulingConfig;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (!_schedulingConfig.Enabled)
            return "Error: Scheduling is disabled for this deployment.";

        if (string.IsNullOrWhiteSpace(args.ReminderId))
            return "Error: 'reminderId' is required.";

        var id = new ReminderId(args.ReminderId);
        var response = await _reminderManager.Ask<ReminderCancelledResponse>(
            new CancelReminderCommand(id), TimeSpan.FromSeconds(10), ct);

        return response.Found
            ? $"Reminder '{args.ReminderId}' cancelled (disabled). The definition is preserved on disk."
            : $"Reminder '{args.ReminderId}' not found.";
    }
}
