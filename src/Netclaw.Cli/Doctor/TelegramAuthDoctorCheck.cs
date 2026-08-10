// -----------------------------------------------------------------------
// <copyright file="TelegramAuthDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli.Telegram;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class TelegramAuthDoctorCheck(NetclawPaths paths, ITelegramProbe telegramProbe) : IDoctorCheck
{
    private const string CheckName = "Telegram Auth";

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, readError) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (readError is not null)
            return DoctorCheckResult.Pass(CheckName, "Skipped (base config is missing or invalid).");

        if (root!["Telegram"] is not JsonObject telegram || !DoctorJsonConfigReader.ReadBool(telegram, "Enabled"))
            return DoctorCheckResult.Pass(CheckName, "Telegram is disabled.");

        var (botToken, tokenReadError) = TelegramDoctorConfig.ReadBotToken(paths);
        if (!string.IsNullOrWhiteSpace(tokenReadError))
        {
            return DoctorCheckResult.Error(
                CheckName,
                tokenReadError,
                "Ensure ~/.netclaw/keys exists for this user, then re-enter the Telegram token via `netclaw config`.");
        }

        if (string.IsNullOrWhiteSpace(botToken))
        {
            return DoctorCheckResult.Error(
                CheckName,
                "Telegram is enabled but no bot token was found.",
                "Run `netclaw config` and enter the bot token from BotFather.");
        }

        var result = await telegramProbe.ProbeAsync(botToken, cancellationToken);
        if (!result.Success)
        {
            return DoctorCheckResult.Error(
                CheckName,
                result.ErrorMessage ?? "Telegram authentication failed.",
                "Check the Telegram bot token in `netclaw config`.");
        }

        var allowedChats = DoctorJsonConfigReader.ReadStringArray(telegram, "AllowedChatIds");
        if (allowedChats.Count > 0)
        {
            var chatResult = await telegramProbe.ResolveChatIdsAsync(botToken, allowedChats, cancellationToken);
            if (!chatResult.Success || chatResult.Unresolved.Count > 0)
            {
                var unresolved = chatResult.Unresolved.Count > 0
                    ? string.Join(", ", chatResult.Unresolved)
                    : "the configured groups";
                return DoctorCheckResult.Error(
                    CheckName,
                    chatResult.ErrorMessage ?? $"Telegram could not access: {unresolved}.",
                    "Check that each group ID is correct and that the bot is still a member of each group.");
            }
        }

        var identity = string.IsNullOrWhiteSpace(result.BotUsername)
            ? "Telegram bot authenticated and configured groups are accessible."
            : $"Telegram bot authenticated (@{result.BotUsername}); configured groups are accessible.";
        return DoctorCheckResult.Pass(CheckName, identity);
    }
}
