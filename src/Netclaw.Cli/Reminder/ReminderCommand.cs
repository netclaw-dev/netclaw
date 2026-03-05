using System.Net.Http.Json;
using System.Text.Json;

namespace Netclaw.Cli.Reminder;

/// <summary>
/// Handles <c>netclaw reminder</c> CLI subcommands: list, create, cancel, show.
/// All commands require the daemon to be running (HTTP to REST API).
/// </summary>
internal static class ReminderCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
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
            "cancel" => await RunCancelAsync(client, baseUrl, args),
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

    private static async Task<int> RunCancelAsync(HttpClient client, string baseUrl, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw reminder cancel <id>");
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
        Console.WriteLine("  cancel <id>                                   Cancel a reminder");
        Console.WriteLine("  show <id>                                     Show reminder details");
        Console.WriteLine();
        Console.WriteLine("Schedule types: once, interval, cron");
        Console.WriteLine("Schedule examples: '30m', '2h', '1d', '0 */6 * * *'");
        Console.WriteLine();
        Console.WriteLine("Requires daemon to be running (netclaw daemon start).");
        return 0;
    }
}
