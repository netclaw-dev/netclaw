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
        // netclaw reminder create <name> <scheduleType> <schedule> "<prompt>" [--channel <id>]
        if (args.Length < 6)
        {
            Console.Error.WriteLine("Usage: netclaw reminder create <name> <scheduleType> <schedule> \"<prompt>\" [--channel <id>]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  scheduleType: once, interval, cron");
            Console.Error.WriteLine("  schedule:     '30m', '2h', '0 */6 * * *', etc.");
            return 1;
        }

        var name = args[2];
        var scheduleType = args[3];
        var schedule = args[4];
        var prompt = args[5];
        string? channel = null;

        for (var i = 6; i < args.Length; i++)
        {
            if (args[i] is "--channel" && i + 1 < args.Length)
            {
                channel = args[++i];
            }
        }

        var body = new
        {
            name,
            prompt,
            scheduleType,
            schedule,
            reportToChannel = channel
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

    private static int WriteHelp()
    {
        Console.WriteLine("Usage: netclaw reminder <subcommand>");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  list                                          List all active reminders");
        Console.WriteLine("  create <name> <type> <schedule> \"<prompt>\"    Create a reminder");
        Console.WriteLine("  delete <id>                                   Delete a reminder");
        Console.WriteLine("  disable <id>                                  Disable a reminder");
        Console.WriteLine("  enable <id>                                   Enable a reminder");
        Console.WriteLine("  import <file> [--replace|--upsert]            Import one reminder file");
        Console.WriteLine("  validate <file>                               Validate reminder file");
        Console.WriteLine("  show <id>                                     Show reminder details");
        Console.WriteLine();
        Console.WriteLine("Schedule types: once, interval, cron");
        Console.WriteLine("Schedule examples: '30m', '2h', '1d', '0 */6 * * *'");
        Console.WriteLine();
        Console.WriteLine("Requires daemon to be running (netclaw daemon start).");
        return 0;
    }
}
