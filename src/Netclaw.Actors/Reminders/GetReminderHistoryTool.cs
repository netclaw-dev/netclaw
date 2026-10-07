// -----------------------------------------------------------------------
// <copyright file="GetReminderHistoryTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Akka.Actor;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// LLM tool for querying recent execution history for a reminder.
/// Returns timestamps, success/failure status, duration, and session IDs so the
/// agent can reason about job health and drill into specific sessions if needed.
/// </summary>
[NetclawTool("get_reminder_history",
    "Get recent execution history for a reminder. Returns timestamps, success/failure, duration, and session IDs for past runs. Use the session_id to drill into a specific execution.",
    Grant = "scheduling")]
public sealed partial class GetReminderHistoryTool : NetclawTool<GetReminderHistoryTool.Params>
{
    private const int MaxRecordsHardCap = 100;

    private readonly SchedulingConfig _schedulingConfig;
    private readonly IActorRef _reminderManager;

    public record Params(
        [property: Description("The reminder ID to fetch history for (use list_reminders to find IDs).")]
        string ReminderId,
        [property: Description("Maximum number of records to return. Defaults to 20, capped at 100.")]
        int? Last = null);

    /// <summary>
    /// Constructs the tool. History always reads through
    /// <paramref name="reminderManager"/>, the same way the other reminder tools do,
    /// so the manager's audience check applies here too — history for an id the
    /// caller cannot see must never be returned.
    /// </summary>
    public GetReminderHistoryTool(
        SchedulingConfig schedulingConfig,
        IActorRef reminderManager)
    {
        _schedulingConfig = schedulingConfig;
        _reminderManager = reminderManager;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (!_schedulingConfig.Enabled)
            return "Error: Scheduling is disabled for this deployment.";

        if (string.IsNullOrWhiteSpace(args.ReminderId))
            return "Error: 'reminder_id' is required.";

        var id = new ReminderId(args.ReminderId);
        var maxRecords = Math.Clamp(args.Last ?? 20, 1, MaxRecordsHardCap);

        var response = await _reminderManager.Ask<ReminderHistoryResponse>(
            new GetReminderHistoryQuery(
                id,
                maxRecords,
                new ReminderAudienceAuthorizationContext(context.Audience, context.SessionId ?? context.ChannelType)),
            TimeSpan.FromSeconds(10),
            ct);

        if (!response.Found)
            return $"No execution history found for reminder '{args.ReminderId}'.";

        var records = response.Records;
        if (records.Count == 0)
            return $"No execution history found for reminder '{args.ReminderId}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"Execution history for '{args.ReminderId}' (last {records.Count} runs):");
        sb.AppendLine();

        foreach (var r in records)
        {
            sb.AppendLine($"  fired_at:    {r.FiredAt:u}");
            sb.AppendLine($"  success:     {r.Success}");
            sb.AppendLine($"  duration_ms: {r.DurationMs}");
            sb.AppendLine($"  session_id:  {r.SessionId}");
            if (r.ErrorMessage is not null)
                sb.AppendLine($"  error:       {r.ErrorMessage}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
