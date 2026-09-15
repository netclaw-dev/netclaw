// -----------------------------------------------------------------------
// <copyright file="TelegramAclDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class TelegramAclDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "Telegram ACL";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, readError) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (readError is not null)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "Skipped (base config is missing or invalid)."));

        if (root!["Telegram"] is not JsonObject telegram || !DoctorJsonConfigReader.ReadBool(telegram, "Enabled"))
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "Telegram is disabled."));

        var allowedChats = DoctorJsonConfigReader.ReadStringArray(telegram, "AllowedChatIds");
        var allowedUsers = DoctorJsonConfigReader.ReadStringArray(telegram, "AllowedUserIds");
        var allowDirectMessages = DoctorJsonConfigReader.ReadBool(telegram, "AllowDirectMessages");

        if (allowDirectMessages && allowedUsers.Count == 0)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                "Telegram direct messages are enabled with no user allow-list; any Telegram user can message the bot.",
                "Set `Telegram:AllowedUserIds` in `netclaw config` to restrict direct-message access."));
        }

        if (allowedChats.Count == 0 && !allowDirectMessages)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                "Telegram has no restricted destination that can receive messages.",
                "Add a group chat ID, or enable direct messages and add a private user ID in `netclaw config`."));
        }

        return Task.FromResult(DoctorCheckResult.Pass(CheckName, "Telegram access has an explicit user or chat scope."));
    }
}
