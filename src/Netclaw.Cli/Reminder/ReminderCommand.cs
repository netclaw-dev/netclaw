using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Actors.Reminders;

namespace Netclaw.Cli.Reminder;

/// <summary>
/// Handles <c>netclaw reminder</c> CLI subcommands.
/// All commands require the daemon to be running (HTTP to REST API).
/// </summary>
internal static class ReminderCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1)
        {
            WriteHelp();
            return 0;
        }

        var subcommand = args.Length > 1 ? args[1] : "help";
        if (subcommand is "help" or "-h" or "--help")
        {
            WriteHelp();
            return 0;
        }

        var endpoint = Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT")
            ?? "http://127.0.0.1:5199";
        var baseUrl = $"{endpoint.TrimEnd('/')}/api/reminders";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        return subcommand switch
        {
            "list" => await RunListAsync(client, baseUrl),
            "create" => await RunCreateAsync(client, baseUrl, args),
            "cancel" or "delete" => await RunDeleteAsync(client, baseUrl, args),
            "disable" => await RunDisableAsync(client, baseUrl, args),
            "enable" => await RunEnableAsync(client, baseUrl, args),
            "import" => await RunImportAsync(client, baseUrl, args),
            "validate" => RunValidate(args),
            "show" => await RunShowAsync(client, baseUrl, args),
            "history" => await RunHistoryAsync(client, baseUrl, args),
            _ => WriteHelp()
        };
    }

    private static async Task<int> RunListAsync(HttpClient client, string baseUrl)
    {
        try
        {
            var response = await client.GetAsync(baseUrl);
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

    private static async Task<int> RunCreateAsync(HttpClient client, string baseUrl, string[] args)
    {
        // netclaw reminder create <id> <scheduleType> <schedule> "<prompt>" [--name <title>] [--target <#channel|@user|id>] [--channel <id>]
        if (args.Length < 6)
        {
            Console.Error.WriteLine("Usage: netclaw reminder create <id> <scheduleType> <schedule> \"<prompt>\" [--name <title>] [--target <#channel|@user|id>] [--channel <id>]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  id:           Stable reminder identifier (kebab-case slug, e.g. 'daily-standup')");
            Console.Error.WriteLine("  scheduleType: once, interval, cron");
            Console.Error.WriteLine("  schedule:     '30m', '2h', '0 */6 * * *', etc.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("If a reminder with the given ID already exists, it will be updated.");
            return 1;
        }

        var id = args[2];
        var scheduleType = args[3];
        var schedule = args[4];
        var prompt = args[5];
        string? name = null;
        string? channel = null;
        string? reportTarget = null;

        for (var i = 6; i < args.Length; i++)
        {
            if (args[i] is "--name" && i + 1 < args.Length)
            {
                name = args[++i];
            }
            else if (args[i] is "--channel" && i + 1 < args.Length)
            {
                channel = args[++i];
            }
            else if (args[i] is "--target" && i + 1 < args.Length)
            {
                reportTarget = args[++i];
            }
        }

        name ??= id;
        reportTarget ??= channel;

        var body = new
        {
            id,
            name,
            prompt,
            scheduleType,
            schedule,
            reportToChannel = channel,
            reportTarget
        };

        try
        {
            var response = await client.PostAsJsonAsync(baseUrl, body);
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

    private static async Task<int> RunDeleteAsync(HttpClient client, string baseUrl, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder delete <id>");
            return 1;
        }

        var id = args[2];
        try
        {
            var response = await client.DeleteAsync($"{baseUrl}/{id}");
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
            return response.StatusCode == System.Net.HttpStatusCode.NotFound ? 1 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] unable to reach daemon: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunDisableAsync(HttpClient client, string baseUrl, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder disable <id>");
            return 1;
        }

        return await RunSimplePostByIdAsync(client, baseUrl, args[2], "disable");
    }

    private static async Task<int> RunEnableAsync(HttpClient client, string baseUrl, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder enable <id>");
            return 1;
        }

        return await RunSimplePostByIdAsync(client, baseUrl, args[2], "enable");
    }

    private static async Task<int> RunSimplePostByIdAsync(HttpClient client, string baseUrl, string id, string action)
    {
        try
        {
            var response = await client.PostAsync($"{baseUrl}/{id}/{action}", content: null);
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

    private static async Task<int> RunImportAsync(HttpClient client, string baseUrl, string[] args)
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
            var response = await client.PostAsJsonAsync($"{baseUrl}/import", new
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

    private static int RunValidate(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder validate <file>");
            return 1;
        }

        var filePath = args[2];
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[FAIL] file not found: {filePath}");
            return 1;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var definition = JsonSerializer.Deserialize<ReminderDefinition>(json, JsonOptions);
            if (definition is null)
            {
                Console.Error.WriteLine("[FAIL] file does not contain a reminder definition.");
                return 1;
            }

            var validationError = ValidateDefinition(definition);
            if (validationError is not null)
            {
                Console.Error.WriteLine($"[FAIL] {validationError}");
                return 1;
            }

            Console.WriteLine($"Reminder definition is valid: {definition.Id}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] invalid JSON: {ex.Message}");
            return 1;
        }
    }

    private static string? ValidateDefinition(ReminderDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            return "Reminder id is required.";
        if (string.IsNullOrWhiteSpace(definition.Title))
            return "Reminder title is required.";
        if (string.IsNullOrWhiteSpace(definition.Instructions))
            return "Reminder instructions are required.";
        if (string.IsNullOrWhiteSpace(definition.NotifyInstructions))
            return "Reminder notifyInstructions is required.";
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

    private static async Task<int> RunShowAsync(HttpClient client, string baseUrl, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder show <id>");
            return 1;
        }

        var id = args[2];
        try
        {
            var response = await client.GetAsync($"{baseUrl}/{id}");
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

    private static async Task<int> RunHistoryAsync(HttpClient client, string baseUrl, string[] args)
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
            var response = await client.GetAsync($"{baseUrl}/{id}/history?last={last}");

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

    private static int WriteHelp()
    {
        Console.WriteLine("Usage: netclaw reminder <subcommand>");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  list                                          List all active reminders");
        Console.WriteLine("  create <id> <type> <schedule> \"<prompt>\"      Create or update a reminder");
        Console.WriteLine("  delete <id>                                   Delete a reminder");
        Console.WriteLine("  disable <id>                                  Disable a reminder");
        Console.WriteLine("  enable <id>                                   Enable a reminder");
        Console.WriteLine("  import <file> [--replace|--upsert]            Import one reminder file");
        Console.WriteLine("  validate <file>                               Validate reminder file");
        Console.WriteLine("  show <id>                                     Show reminder details");
        Console.WriteLine("  history <id> [--last N]                       Show recent execution history (default: 20)");
        Console.WriteLine();
        Console.WriteLine("Create options:");
        Console.WriteLine("  --name    <title>                              Human-readable title (defaults to <id>)");
        Console.WriteLine("  --target  <#channel|@user|C...|U...>          Human-friendly or canonical Slack target");
        Console.WriteLine("  --channel <id>                                 Back-compat alias (channel id or name)");
        Console.WriteLine();
        Console.WriteLine("If a reminder with the given ID already exists, it will be updated (upsert).");
        Console.WriteLine();
        Console.WriteLine("Schedule types: once, interval, cron");
        Console.WriteLine("Schedule examples: '30m', '2h', '1d', '0 */6 * * *'");
        Console.WriteLine();
        Console.WriteLine("Requires daemon to be running (netclaw daemon start).");
        return 0;
    }
}
