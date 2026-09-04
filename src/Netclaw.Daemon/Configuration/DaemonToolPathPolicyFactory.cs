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
        var sqlitePath = paths.SqliteDbPath;
        var sqliteSidecars = new[]
        {
            sqlitePath + "-wal",
            sqlitePath + "-shm",
            sqlitePath + "-journal"
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
            sqlitePath,
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
            paths.ToolApprovalsPath,
            paths.HardDenyOverridesPath,
            paths.DaemonEnvironmentFilePath,
            paths.DevicesPath,
            paths.BootstrapStatePath,
            sqlitePath,
            ..sqliteSidecars,
            ..processControlPaths,
            paths.ToolingShadowDirectory,
        ];
        string[] shellIndicatorList =
        [
            paths.ConfigDirectory,
            paths.SecretsPath,
            paths.WebhooksDirectory,
            paths.KeysDirectory,
            sqlitePath,
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
