using System.Text.Json.Nodes;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class SlackAuthDoctorCheck(NetclawPaths paths, ISlackProbe slackProbe) : IDoctorCheck
{
    private const string CheckName = "Slack Auth";

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, readError) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (readError is not null)
            return DoctorCheckResult.Pass(CheckName, "Skipped (base config is missing or invalid).");

        if (root!["Slack"] is not JsonObject slack || !ReadBool(slack, "Enabled"))
            return DoctorCheckResult.Pass(CheckName, "Slack is disabled.");

        // Read bot token from secrets.json
        var botToken = ReadBotToken(paths);
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return DoctorCheckResult.Error(CheckName,
                "Slack is enabled but no bot token found.",
                "Run `netclaw init` or add Slack:BotToken to secrets.json.");
        }

        var result = await slackProbe.ProbeAsync(botToken, cancellationToken);
        if (result.Success)
            return DoctorCheckResult.Pass(CheckName, $"Bot authenticated (team: {result.TeamName}).");

        return DoctorCheckResult.Error(CheckName, result.ErrorMessage!,
            "Check your Slack app's Bot User OAuth Token and scopes.");
    }

    private static string? ReadBotToken(NetclawPaths paths)
    {
        if (!File.Exists(paths.SecretsPath))
            return null;

        try
        {
            var secrets = JsonNode.Parse(File.ReadAllText(paths.SecretsPath)) as JsonObject;
            return secrets?["Slack"]?["BotToken"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadBool(JsonObject obj, string property)
        => obj[property] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
