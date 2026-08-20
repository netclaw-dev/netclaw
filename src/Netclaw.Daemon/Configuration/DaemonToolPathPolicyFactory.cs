// -----------------------------------------------------------------------
// <copyright file="DaemonToolPathPolicyFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Daemon.Configuration;

internal static class DaemonToolPathPolicyFactory
{
    public static ToolPathPolicy Create(
        NetclawPaths paths,
        ShellExecutionEnvironment shellEnvironment)
    {
        var sqliteSidecars = new[]
        {
            paths.SqliteDbPath + "-wal",
            paths.SqliteDbPath + "-shm",
            paths.SqliteDbPath + "-journal"
        };
        var processControlPaths = new[]
        {
            paths.PidFilePath,
            paths.LockFilePath,
            paths.RestartManifestPath
        };

        string[] writeDenyList =
        [
            paths.ConfigDirectory,
            paths.SecretsPath,
            paths.KeysDirectory,
            paths.SqliteDbPath,
            ..sqliteSidecars,
            ..processControlPaths,
            paths.SystemSkillsDirectory,
            paths.ServerFeedsDirectory,
            paths.ToolingShadowDirectory,
        ];
        string[] readDenyList =
        [
            paths.SecretsPath,
            paths.KeysDirectory,
            paths.WebhooksDirectory,
            paths.ToolingShadowDirectory,
        ];
        string[] shellIndicatorList =
        [
            paths.ConfigDirectory,
            paths.SecretsPath,
            paths.WebhooksDirectory,
            paths.KeysDirectory,
            paths.SqliteDbPath,
            ..sqliteSidecars,
            ..processControlPaths,
            paths.ToolingShadowDirectory,
        ];

        return new ToolPathPolicy(
            shellEnvironment,
            writeDenyList,
            readDenyList,
            shellIndicatorList);
    }
}
