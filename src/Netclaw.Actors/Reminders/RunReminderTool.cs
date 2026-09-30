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
/// LLM tool that runs an existing reminder now, outside its schedule, so an
/// agent can test a reminder on request. Sends the same
/// <see cref="RunReminderNowCommand"/> / <see cref="AwaitReminderRunCommand"/>
/// pair the <c>netclaw reminder run</c> CLI command sends, and reads the same
/// <see cref="ReminderRunNowResponse"/> result — one run path, two callers.
/// </summary>
[NetclawTool("run_reminder",
    "Run an existing reminder now to test it. The reminder's schedule does not change: a one-shot " +
    "reminder stays scheduled and a recurring reminder keeps its next fire time. The run uses the " +
    "reminder's real delivery target and is recorded in its history as a manual run. Returns the " +
    "result of the run.",
    Grant = "scheduling")]
public sealed partial class RunReminderTool : NetclawTool<RunReminderTool.Params>
{
    /// <summary>Ask timeout for the fast accept/reject ack from <see cref="HandleRunNow"/>.</summary>
    private static readonly TimeSpan AcceptAckTimeout = TimeSpan.FromSeconds(10);

    private readonly IActorRef _reminderManager;
    private readonly SchedulingConfig _schedulingConfig;

    public record Params(
        [property: Description("The reminder ID to run now (returned by set_reminder or list_reminders).")]
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

        var id = new ReminderId(args.Id);

        // The audience form of ReminderAudienceAuthorizationContext. HandleRunNow
        // treats a reminder whose stored audience exceeds this session's audience
        // as not found — it never reveals that an out-of-scope reminder exists.
        var authorization = new ReminderAudienceAuthorizationContext(context.Audience, context.SessionId ?? context.ChannelType);

        var accepted = await _reminderManager.Ask<ReminderRunNowResponse>(
            new RunReminderNowCommand(id, authorization), AcceptAckTimeout, ct);

        if (!accepted.Success)
            return DescribeRejection(args.Id, accepted);

        // Deadlock guard: a reminder that replies into the SAME session that
        // called this tool cannot settle until this tool call's own turn ends —
        // its reply is a new turn queued behind this one, and this tool cannot
        // return (ending this turn) until it stops waiting. accepted.SessionId
        // is the exact session the run will deliver to: for current-session
        // delivery it is the reminder's fixed target session; for channel/none
        // delivery it is a synthetic per-run id that cannot collide with a real
        // session id. Comparing the two session ids is enough to detect the
        // collision without inspecting delivery kind directly.
        if (!string.IsNullOrWhiteSpace(context.SessionId)
            && string.Equals(accepted.SessionId, context.SessionId, StringComparison.Ordinal))
        {
            return DescribeSelfDeliveryStarted(args.Id, accepted);
        }

        try
        {
            var settled = await _reminderManager.Ask<ReminderRunNowResponse>(
                new AwaitReminderRunCommand(id, accepted.ExecutionId!.Value), ManualRunMaxWaitTimeout, ct);
            return DescribeSettled(args.Id, settled);
        }
        catch (AskTimeoutException)
        {
            return DescribeStillRunning(args.Id, accepted);
        }
    }

    private static string DescribeRejection(string id, ReminderRunNowResponse rejected) => rejected.Error switch
    {
        ReminderRunError.SchedulingDisabled => "Error: Scheduling is disabled for this deployment.",
        ReminderRunError.NotFound => $"Reminder '{id}' not found.",
        ReminderRunError.Disabled => $"Reminder '{id}' is disabled.",
        ReminderRunError.Expired => $"Reminder '{id}' has expired.",
        ReminderRunError.AlreadyExecuting =>
            $"Reminder '{id}' is already executing. Wait for it to finish and try again.",
        ReminderRunError.Unauthorized =>
            $"Error: {rejected.ErrorMessage ?? "Running a reminder now requires a session audience."}",
        _ => $"Error: {rejected.ErrorMessage ?? $"Unable to run reminder '{id}'."}"
    };

    private static string DescribeSelfDeliveryStarted(string id, ReminderRunNowResponse accepted) =>
        $"Reminder '{id}' started (execution {accepted.ExecutionId}).\n" +
        "This session is the reminder's own delivery target. " +
        "The reply arrives as a new turn after this turn ends.\n" +
        "This tool did not wait for it, to avoid blocking this turn.\n" +
        $"Session: {accepted.SessionId}\n" +
        $"Delivery: {accepted.DeliveryTarget ?? "none"}";

    private static string DescribeStillRunning(string id, ReminderRunNowResponse accepted) =>
        $"Reminder '{id}' is still running after {ManualRunMaxWaitTimeout}.\n" +
        "The run was not cancelled; it keeps running in the background.\n" +
        $"Session: {accepted.SessionId}\n" +
        $"Delivery: {accepted.DeliveryTarget ?? "none"}\n" +
        "Use get_reminder_history to check the result, or resume the session to see it.";

    private static string DescribeSettled(string id, ReminderRunNowResponse settled)
    {
        var status = settled.Success ? "ok" : "failed";
        var duration = settled.DurationMs is { } ms ? $"{ms} ms" : "unknown";

        var lines = new List<string>
        {
            $"Reminder '{id}' run: {status}",
            $"Duration: {duration}",
            $"Session: {settled.SessionId ?? "unknown"}",
            $"Delivery: {settled.DeliveryTarget ?? "none"}"
        };

        if (settled.Success)
        {
            lines.Add("Reply:");
            lines.Add(string.IsNullOrWhiteSpace(settled.ReplyText)
                ? "(no reply text captured for this delivery kind)"
                : settled.ReplyText);
            if (settled.ReplyTextTrimmed)
                lines.Add("(reply text trimmed to fit)");
        }
        else
        {
            lines.Add($"Error: {settled.ErrorMessage ?? "Manual reminder run failed."}");
        }

        return string.Join('\n', lines);
    }
}
