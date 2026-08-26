// -----------------------------------------------------------------------
// <copyright file="SlackAuthDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Channels.Slack;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Doctor;

public sealed class SlackAuthDoctorCheck(NetclawPaths paths, ISlackProbe slackProbe) : IDoctorCheck
{
    private const string CheckName = "Slack Auth";

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, readError) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (readError is not null)
            return DoctorCheckResult.Pass(CheckName, "Skipped (base config is missing or invalid).");

        if (root!["Slack"] is not JsonObject slack || !DoctorJsonConfigReader.ReadBool(slack, "Enabled"))
            return DoctorCheckResult.Pass(CheckName, "Slack is disabled.");

        // Read bot token from secrets.json
        var (botToken, tokenReadError) = ReadBotToken(paths);
        if (!string.IsNullOrWhiteSpace(tokenReadError))
        {
            return DoctorCheckResult.Error(CheckName,
                tokenReadError,
                "Ensure ~/.netclaw/keys exists for this user, then re-enter Slack tokens via `netclaw init`.");
        }

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

    private static (string? Token, string? Error) ReadBotToken(NetclawPaths paths)
    {
        if (!File.Exists(paths.SecretsPath))
            return (null, null);

        try
        {
            var secrets = JsonNode.Parse(File.ReadAllText(paths.SecretsPath)) as JsonObject;
            var raw = secrets?["Slack"]?["BotToken"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return (null, null);

            if (!ISecretsProtector.IsEncrypted(raw))
                return (raw, null);

            try
            {
                return (ConfigFileHelper.DecryptIfEncrypted(paths, raw), null);
            }
            catch (Exception ex)
            {
                return (null,
                    $"Slack bot token is present but could not be decrypted: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return (null, $"Failed reading Slack bot token from secrets.json: {ex.Message}");
        }
    }

}
