// -----------------------------------------------------------------------
// <copyright file="GetReminderHistoryTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Tools;

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

    private readonly ReminderHistoryStore _historyStore;
    private readonly SchedulingConfig _schedulingConfig;

    public record Params(
        [property: Description("The reminder ID to fetch history for (use list_reminders to find IDs).")]
        string ReminderId,
        [property: Description("Maximum number of records to return. Defaults to 20, capped at 100.")]
        int? Last = null);

    public GetReminderHistoryTool(ReminderHistoryStore historyStore, SchedulingConfig schedulingConfig)
    {
        _historyStore = historyStore;
        _schedulingConfig = schedulingConfig;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (!_schedulingConfig.Enabled)
            return "Error: Scheduling is disabled for this deployment.";

        if (string.IsNullOrWhiteSpace(args.ReminderId))
            return "Error: 'reminder_id' is required.";

        var id = new ReminderId(args.ReminderId);
        var maxRecords = Math.Clamp(args.Last ?? 20, 1, MaxRecordsHardCap);

        var records = await _historyStore.ReadAsync(id, maxRecords);

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
