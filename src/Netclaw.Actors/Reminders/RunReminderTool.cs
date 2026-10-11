// -----------------------------------------------------------------------
// <copyright file="RunReminderTool.cs" company="Petabridge, LLC">
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
/// Returns the exact prompt of an existing reminder so that the agent can run it
/// now, in this chat, with a person who answers approval prompts. The scheduled
/// run is unattended and cannot prompt. This attended test lets the person save
/// "Always" grants that the scheduled run then reads.
/// </summary>
/// <remarks>
/// The test must use the reminder's real permissions. The manager hides a
/// reminder above the caller's audience (it reads as not found). This tool also
/// refuses a reminder below the caller's audience: a wider chat would pass calls
/// that the scheduled run denies, so the test would pass falsely.
/// </remarks>
[NetclawTool("run_reminder",
    "Test an existing reminder now, in this chat. Returns the reminder's exact scheduled prompt. " +
    "Carry out that prompt in this chat so the user can answer approval prompts and save grants " +
    "for the unattended scheduled run. The schedule and the reminder history do not change.",
    Grant = "scheduling")]
public sealed partial class RunReminderTool : NetclawTool<RunReminderTool.Params>
{
    private readonly IActorRef _reminderManager;
    private readonly SchedulingConfig _schedulingConfig;

    public record Params(
        [property: Description("The reminder ID to test (returned by set_reminder or list_reminders).")]
        string Id);

    public RunReminderTool(IActorRef reminderManager, SchedulingConfig schedulingConfig)
    {
        _reminderManager = reminderManager;
        _schedulingConfig = schedulingConfig;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (!_schedulingConfig.Enabled)
            return "Error: Scheduling is disabled for this deployment.";

        if (string.IsNullOrWhiteSpace(args.Id))
            return "Error: 'id' is required.";

        // An unattended run has nobody to answer a prompt, so it cannot save a grant.
        if (context.RunScope.InteractiveApproval is not InteractiveApprovalCapability.Available)
            return "Error: run_reminder needs a chat where a person can answer approval prompts.";

        var response = await _reminderManager.Ask<GetReminderResponse>(
            new GetReminderCommand(
                new ReminderId(args.Id),
                new ReminderAudienceAuthorizationContext(context.Audience, context.SessionId ?? context.ChannelType)),
            TimeSpan.FromSeconds(10),
            ct);

        if (response.Reminder is not { } reminder)
            return $"Error: Reminder '{args.Id}' not found.";

        if (reminder.Audience is not { } reminderAudience)
            return $"Error: Reminder '{args.Id}' has no stored audience. Recreate it before you test it.";

        if (reminderAudience != context.Audience)
        {
            var audience = reminderAudience.ToWireValue();
            return $"Error: Reminder '{args.Id}' runs with the {audience} audience, but this chat has the " +
                   $"{context.Audience.ToWireValue()} audience. A test here would have wider permissions than " +
                   $"the scheduled run. Run this in a {audience} chat to test it with its real permissions.";
        }

        return $"""
            Reminder '{args.Id}' ({reminder.Title}). Carry out the reminder prompt below now, in this chat.
            Follow it exactly, as the scheduled run would. The steps are real.
            Approval prompts go to the person in this chat.

            --- reminder prompt ---
            {ReminderExecutionActor.BuildPrompt(reminder)}
            --- end of reminder prompt ---
            """;
    }
}
