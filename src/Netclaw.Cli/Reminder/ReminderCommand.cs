// -----------------------------------------------------------------------
// <copyright file="ReminderCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Actors.Reminders;
using Netclaw.Cli.Daemon;

namespace Netclaw.Cli.Reminder;

/// <summary>
/// Handles <c>netclaw reminder</c> CLI subcommands.
/// All commands require the daemon to be running (HTTP to REST API via <see cref="DaemonApi"/>).
/// </summary>
internal static class ReminderCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(
        string[] args,
        DaemonApi? daemonApi,
        TextWriter output,
        TextWriter error)
    {
        if (args.Length == 1)
        {
            WriteHelp(output);
            return 0;
        }

        var subcommand = args.Length > 1 ? args[1] : "help";
        if (subcommand is "help" or "-h" or "--help")
        {
            WriteHelp(output);
            return 0;
        }

        // None of the subcommands below have their own --help handling, so a trailing
        // --help/-h was previously ignored and the subcommand ran for real — e.g.
        // `reminder list --help` still hit the live daemon and printed reminders instead
        // of help (same missed-help pattern reported for `netclaw memory backfill-embeddings
        // --help`). Scan the full argument list, not just the subcommand slot.
        if (CliArgsParser.HasTrailingHelpToken(args, startIndex: 2))
        {
            WriteHelp(output);
            return 0;
        }

        // validate is offline — no daemon needed
        if (subcommand is "validate")
            return RunValidate(args, output, error);

        if (daemonApi is null)
        {
            error.WriteLine($"[FAIL] reminder {subcommand}: requires a running daemon.");
            error.WriteLine("       fix: run `netclaw daemon start` and retry.");
            return 1;
        }

        return subcommand switch
        {
            "list" => await RunListAsync(daemonApi),
            "create" => await RunCreateAsync(daemonApi, args),
            "cancel" => await RunCancelAsync(daemonApi, args),
            "delete" => await RunDeleteAsync(daemonApi, args),
            "disable" => await RunDisableAsync(daemonApi, args),
            "enable" => await RunEnableAsync(daemonApi, args),
            "import" => await RunImportAsync(daemonApi, args),
            "show" => await RunShowAsync(daemonApi, args),
            "history" => await RunHistoryAsync(daemonApi, args),
            "status" => await RunStatusAsync(daemonApi, args),
            _ => WriteHelp(output)
        };
    }

    private static async Task<int> RunListAsync(DaemonApi api)
    {
        try
        {
            using var response = await api.ListRemindersAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[FAIL] daemon returned {(int)response.StatusCode}");
                return 1;
            }

            var json = await response.Content.ReadAsStringAsync();
            var reminders = JsonSerializer.Deserialize<JsonElement>(json);

            if (reminders.ValueKind == JsonValueKind.Array && reminders.GetArrayLength() == 0)
            {
                Console.WriteLine("No active reminders.");
                return 0;
            }

            Console.WriteLine(JsonSerializer.Serialize(reminders, JsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] unable to reach daemon: {ex.Message}");
            Console.Error.WriteLine("       fix: run `netclaw daemon start` and retry.");
            return 1;
        }
    }

    private static async Task<int> RunCreateAsync(DaemonApi api, string[] args)
    {
        // netclaw reminder create <id> <scheduleType> <schedule> "<prompt>" [--name <title>] [--delivery <kind>] [--transport <slack>] [--address <target>]
        if (args.Length < 6)
        {
            Console.Error.WriteLine("Usage: netclaw reminder create <id> <scheduleType> <schedule> \"<prompt>\" [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  id:           Stable reminder identifier (kebab-case slug, e.g. 'daily-standup')");
            Console.Error.WriteLine("  scheduleType: once, interval, cron");
            Console.Error.WriteLine("  schedule:     '30m', '2h', '0 */6 * * *', etc.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --name <title>           Human-readable title (defaults to id)");
            Console.Error.WriteLine("  --delivery <kind>        Delivery kind: none, channel (default: none)");
            Console.Error.WriteLine("  --transport <transport>  Transport for channel delivery (e.g. 'slack')");
            Console.Error.WriteLine("  --address <target>       Target for channel delivery (#channel, @user, ID)");
            Console.Error.WriteLine("  --expires-in <duration>  Auto-disable after duration (e.g. '24h', '7d')");
            Console.Error.WriteLine();
            Console.Error.WriteLine("If a reminder with the given ID already exists, it will be updated.");
            return 1;
        }

        var id = args[2];
        var scheduleType = args[3];
        var schedule = args[4];
        var prompt = args[5];
        string? name = null;
        string deliveryKind = "none";
        string? deliveryTransport = null;
        string? deliveryAddress = null;
        string? expiresIn = null;

        for (var i = 6; i < args.Length; i++)
        {
            if (args[i] is "--name" && i + 1 < args.Length)
            {
                name = args[++i];
            }
            else if (args[i] is "--delivery" && i + 1 < args.Length)
            {
                deliveryKind = args[++i];
            }
            else if (args[i] is "--transport" && i + 1 < args.Length)
            {
                deliveryTransport = args[++i];
            }
            else if (args[i] is "--address" && i + 1 < args.Length)
            {
                deliveryAddress = args[++i];
            }
            else if (args[i] is "--expires-in" && i + 1 < args.Length)
            {
                expiresIn = args[++i];
            }
        }

        name ??= id;

        var body = new
        {
            id,
            name,
            prompt,
            scheduleType,
            schedule,
            deliveryKind,
            deliveryTransport,
            deliveryAddress,
            expiresIn
        };

        try
        {
            using var response = await api.CreateReminderAsync(body);
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json);

            if (response.IsSuccessStatusCode)
            {
                if (result.TryGetProperty("message", out var msg))
                    Console.WriteLine(msg.GetString());
                else
                    Console.WriteLine(json);
                return 0;
            }

            if (result.TryGetProperty("error", out var err))
                Console.Error.WriteLine($"[FAIL] {err.GetString()}");
            else
                Console.Error.WriteLine($"[FAIL] {json}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] unable to reach daemon: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunCancelAsync(DaemonApi api, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder cancel <id>");
            return 1;
        }

        return await RunRemoveAsync(api, args[2], permanent: false);
    }

    private static async Task<int> RunDeleteAsync(DaemonApi api, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder delete <id>");
            return 1;
        }

        return await RunRemoveAsync(api, args[2], permanent: true);
    }

    private static async Task<int> RunRemoveAsync(DaemonApi api, string id, bool permanent)
    {
        try
        {
            using var response = await api.DeleteReminderAsync(id, permanent);
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json);

            if (response.IsSuccessStatusCode)
            {
                if (result.TryGetProperty("message", out var msg))
                    Console.WriteLine(msg.GetString());
                else
                    Console.WriteLine(json);
                return 0;
            }

            if (result.TryGetProperty("error", out var err))
                Console.Error.WriteLine($"[FAIL] {err.GetString()}");
            else
                Console.Error.WriteLine($"[FAIL] {json}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] unable to reach daemon: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunDisableAsync(DaemonApi api, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder disable <id>");
            return 1;
        }

        return await RunToggleAsync(api, args[2], enable: false);
    }

    private static async Task<int> RunEnableAsync(DaemonApi api, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder enable <id>");
            return 1;
        }

        return await RunToggleAsync(api, args[2], enable: true);
    }

    private static async Task<int> RunToggleAsync(DaemonApi api, string id, bool enable)
    {
        try
        {
            using var response = enable
                ? await api.EnableReminderAsync(id)
                : await api.DisableReminderAsync(id);
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json);

            if (response.IsSuccessStatusCode)
            {
                if (result.TryGetProperty("message", out var msg))
                    Console.WriteLine(msg.GetString());
                else
                    Console.WriteLine(json);
                return 0;
            }

            if (result.TryGetProperty("error", out var err))
                Console.Error.WriteLine($"[FAIL] {err.GetString()}");
            else
                Console.Error.WriteLine($"[FAIL] {json}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] unable to reach daemon: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunImportAsync(DaemonApi api, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder import <file> [--replace|--upsert]");
            return 1;
        }

        var filePath = args[2];
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[FAIL] file not found: {filePath}");
            return 1;
        }

        var mode = "create";
        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] == "--replace") mode = "replace";
            if (args[i] == "--upsert") mode = "upsert";
        }

        ReminderDefinition? definition;
        try
        {
            var json = File.ReadAllText(filePath);
            definition = JsonSerializer.Deserialize<ReminderDefinition>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] invalid JSON: {ex.Message}");
            return 1;
        }

        if (definition is null)
        {
            Console.Error.WriteLine("[FAIL] file does not contain a reminder definition.");
            return 1;
        }

        var validation = ValidateDefinition(definition);
        if (validation is not null)
        {
            Console.Error.WriteLine($"[FAIL] {validation}");
            return 1;
        }

        try
        {
            using var response = await api.ImportReminderAsync(new
            {
                definition,
                writeMode = mode
            }, JsonOptions);

            var body = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(body);

            if (response.IsSuccessStatusCode)
            {
                if (result.TryGetProperty("message", out var msg))
                    Console.WriteLine(msg.GetString());
                else
                    Console.WriteLine(body);
                return 0;
            }

            if (result.TryGetProperty("error", out var err))
                Console.Error.WriteLine($"[FAIL] {err.GetString()}");
            else
                Console.Error.WriteLine($"[FAIL] {body}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] unable to reach daemon: {ex.Message}");
            return 1;
        }
    }

    private static int RunValidate(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 3)
        {
            error.WriteLine("Usage: netclaw reminder validate <file>");
            return 1;
        }

        var filePath = args[2];
        if (!File.Exists(filePath))
        {
            error.WriteLine($"[FAIL] file not found: {filePath}");
            return 1;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var definition = JsonSerializer.Deserialize<ReminderDefinition>(json, JsonOptions);
            if (definition is null)
            {
                error.WriteLine("[FAIL] file does not contain a reminder definition.");
                return 1;
            }

            var validationError = ValidateDefinition(definition);
            if (validationError is not null)
            {
                error.WriteLine($"[FAIL] {validationError}");
                return 1;
            }

            output.WriteLine($"Reminder definition is valid: {definition.Id}");
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"[FAIL] invalid JSON: {ex.Message}");
            return 1;
        }
    }

    private static string? ValidateDefinition(ReminderDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id.Value))
            return "Reminder id is required.";
        if (string.IsNullOrWhiteSpace(definition.Title))
            return "Reminder title is required.";
        if (string.IsNullOrWhiteSpace(definition.Instructions))
            return "Reminder instructions are required.";
        if (definition.Delivery is null)
            return "Reminder delivery is required.";
        if (definition.Schedule is null)
            return "Reminder schedule is required.";

        switch (definition.Schedule.Type)
        {
            case ReminderScheduleType.OneShot when definition.Schedule.FireAt is null:
                return "One-shot reminders require schedule.fireAtMs.";
            case ReminderScheduleType.Interval when definition.Schedule.IntervalTicks is null:
                return "Interval reminders require schedule.intervalTicks.";
            case ReminderScheduleType.Cron when string.IsNullOrWhiteSpace(definition.Schedule.CronExpression):
                return "Cron reminders require schedule.cronExpression.";
            case ReminderScheduleType.Cron when !CronScheduleHelper.TryParse(definition.Schedule.CronExpression!):
                return "Cron expression is invalid.";
            default:
                return null;
        }
    }

    private static async Task<int> RunShowAsync(DaemonApi api, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder show <id>");
            return 1;
        }

        var id = args[2];
        try
        {
            using var response = await api.GetReminderAsync(id);
            var json = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<JsonElement>(json);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 0;
            }

            var err = JsonSerializer.Deserialize<JsonElement>(json);
            if (err.TryGetProperty("error", out var errMsg))
                Console.Error.WriteLine($"[FAIL] {errMsg.GetString()}");
            else
                Console.Error.WriteLine($"[FAIL] {json}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] unable to reach daemon: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunHistoryAsync(DaemonApi api, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder history <id> [--last N]");
            return 1;
        }

        var id = args[2];
        var last = 20;

        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] == "--last" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n))
            {
                last = n;
                i++;
            }
        }

        try
        {
            using var response = await api.GetReminderHistoryAsync(id, last);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.Error.WriteLine($"[FAIL] Reminder '{id}' not found.");
                return 1;
            }

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[FAIL] daemon returned {(int)response.StatusCode}");
                return 1;
            }

            var json = await response.Content.ReadAsStringAsync();
            var records = JsonSerializer.Deserialize<HistoryRecord[]>(json, JsonOptions);

            if (records is null || records.Length == 0)
            {
                Console.WriteLine($"No execution history recorded for {id}.");
                return 0;
            }

            const int colFiredAt = 25;
            const int colStatus = 8;
            const int colDuration = 12;

            Console.WriteLine($"{"fired_at",-colFiredAt}  {"status",-colStatus}  {"duration_ms",-colDuration}  session_id");
            Console.WriteLine(new string('-', colFiredAt + colStatus + colDuration + 34));

            foreach (var r in records)
            {
                var status = r.Success ? "ok" : "failed";
                Console.WriteLine($"{r.FiredAt:u,-colFiredAt}  {status,-colStatus}  {r.DurationMs,-colDuration}  {r.SessionId}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] unable to reach daemon: {ex.Message}");
            Console.Error.WriteLine("       fix: run `netclaw daemon start` and retry.");
            return 1;
        }
    }

    private static async Task<int> RunStatusAsync(DaemonApi api, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder status <id>");
            return 1;
        }

        var id = args[2];

        try
        {
            using var response = await api.GetReminderStatusAsync(id);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.Error.WriteLine($"[FAIL] Reminder '{id}' not found.");
                return 1;
            }

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[FAIL] daemon returned {(int)response.StatusCode}");
                return 1;
            }

            var json = await response.Content.ReadAsStringAsync();
            var status = JsonSerializer.Deserialize<ReminderStatusView>(json, JsonOptions);
            if (status is null)
            {
                Console.Error.WriteLine("[FAIL] could not parse status response.");
                return 1;
            }

            Console.WriteLine($"Reminder:            {status.Id}");
            Console.WriteLine($"Enabled:             {status.Enabled}");
            Console.WriteLine($"Executing now:       {status.Executing}");
            Console.WriteLine($"Next fire:           {status.NextFire ?? "not scheduled"}");
            Console.WriteLine($"Consecutive fails:   {status.ConsecutiveFailures}");
            Console.WriteLine($"Skipped occurrences: {status.SkippedDuplicates}");
            Console.WriteLine($"Terminal outcome:    {status.TerminalOutcome ?? "none"}");

            if (status.Occurrence is { } occurrence)
            {
                Console.WriteLine($"Occurrence status:   {occurrence.CompletionStatus}");
                Console.WriteLine($"Occurrence attempts: {occurrence.AttemptCount}");
                if (occurrence.NextAttemptAtUtc is { } nextAttemptAtUtc)
                    Console.WriteLine($"Next retry:          {nextAttemptAtUtc:u}");
                if (!string.IsNullOrWhiteSpace(occurrence.LastFailureReason))
                    Console.WriteLine($"Last failure:        {occurrence.LastFailureReason}");
            }

            var history = status.RecentHistory ?? [];
            if (history.Length == 0)
            {
                Console.WriteLine("Recent history:      none");
            }
            else
            {
                Console.WriteLine("Recent history (newest first):");
                // History arrives oldest-first; reverse so the most recent runs
                // (the ones an operator diagnosing a failure cares about) lead.
                foreach (var r in history.Reverse())
                {
                    var outcome = r.Success ? "ok" : "failed";
                    var err = string.IsNullOrEmpty(r.ErrorMessage) ? "" : $" — {r.ErrorMessage}";
                    Console.WriteLine($"  {r.FiredAt:u}  {outcome}{err}");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] unable to reach daemon: {ex.Message}");
            Console.Error.WriteLine("       fix: run `netclaw daemon start` and retry.");
            return 1;
        }
    }

    /// <summary>CLI-side projection of the daemon's reminder status JSON.</summary>
    private sealed record ReminderStatusView(
        string Id,
        bool Enabled,
        bool Executing,
        string? NextFire,
        int ConsecutiveFailures,
        int SkippedDuplicates,
        string? TerminalOutcome,
        ReminderOccurrenceView? Occurrence,
        HistoryRecord[]? RecentHistory);

    private sealed record ReminderOccurrenceView(
        DateTimeOffset DueTimeUtc,
        DateTimeOffset? NextAttemptAtUtc,
        int AttemptCount,
        string? LastFailureReason,
        string CompletionStatus);

    private static int WriteHelp(TextWriter output)
    {
        output.WriteLine("Usage: netclaw reminder <subcommand>");
        output.WriteLine();
        output.WriteLine("Subcommands:");
        output.WriteLine("  list                                          List all active reminders");
        output.WriteLine("  create <id> <type> <schedule> \"<prompt>\"      Create or update a reminder");
        output.WriteLine("  cancel <id>                                   Cancel a reminder (disables, keeps definition)");
        output.WriteLine("  delete <id>                                   Permanently delete a reminder and its history");
        output.WriteLine("  disable <id>                                  Disable a reminder");
        output.WriteLine("  enable <id>                                   Enable a reminder");
        output.WriteLine("  import <file> [--replace|--upsert]            Import one reminder file");
        output.WriteLine("  validate <file>                               Validate reminder file");
        output.WriteLine("  show <id>                                     Show reminder details");
        output.WriteLine("  history <id>                                  Show recent execution history (default: 20)");
        output.WriteLine("  status <id>                                   Show execution, retry, terminal, and history status");
        output.WriteLine();
        output.WriteLine("Create options:");
        output.WriteLine("  --name <title>           Human-readable title (defaults to <id>)");
        output.WriteLine("  --delivery <kind>        Delivery kind: none, channel (default: none)");
        output.WriteLine("  --transport <transport>  Transport for channel delivery (e.g. 'slack')");
        output.WriteLine("  --address <target>       Target for channel delivery (#channel, @user, ID)");
        output.WriteLine("  --expires-in <duration>  Auto-disable after duration (e.g. '24h', '7d')");
        output.WriteLine();
        output.WriteLine("If a reminder with the given ID already exists, it will be updated (upsert).");
        output.WriteLine();
        output.WriteLine("Schedule types: once, interval, cron");
        output.WriteLine("Schedule examples: '30m', '2h', '1d', '0 */6 * * *'");
        output.WriteLine();
        output.WriteLine("Requires daemon to be running (netclaw daemon start).");
        return 0;
    }
}
