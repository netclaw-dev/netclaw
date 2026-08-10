// -----------------------------------------------------------------------
// <copyright file="TelegramDoctorConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Doctor;

internal static class TelegramDoctorConfig
{
    public static (string? Token, string? Error) ReadBotToken(NetclawPaths paths)
    {
        if (!File.Exists(paths.SecretsPath))
            return (null, null);

        try
        {
            var secrets = JsonNode.Parse(File.ReadAllText(paths.SecretsPath)) as JsonObject;
            var raw = secrets?["Telegram"]?["BotToken"]?.GetValue<string>();
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
                return (null, $"Telegram bot token is present but could not be decrypted: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return (null, $"Failed to read the Telegram bot token from secrets.json: {ex.Message}");
        }
    }
}
