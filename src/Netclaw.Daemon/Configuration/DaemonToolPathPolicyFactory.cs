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
        ShellExecutionEnvironment shellEnvironment,
        SkillFeedsConfig skillFeeds)
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

        // Owner decision (2026-10-05): the system skill folder and the server feed
        // folder are agent guidance, as the identity files are. They are not
        // control plane, so they are not on this list. Netclaw cannot tell if a
        // program reads or writes a path argument, so a write entry also denied
        // "bash <skill script>" and "ls <skill folder>". The daemon start restores
        // the system skills, and the feed sync restores a changed feed skill.
        // The sync state of each feed holds the file hashes that the restore
        // compares, so it is an integrity record and stays write-protected. The
        // agent may read it.
        var feedSyncStatePaths = skillFeeds.Feeds
            .SelectMany(feed => new[]
            {
                paths.ServerFeedSyncStatePath(feed.Name),
                paths.ServerFeedAgentSyncStatePath(feed.Name)
            });
        string[] writeDenyList =
        [
            paths.ConfigDirectory,
            paths.SecretsPath,
            paths.KeysDirectory,
            sqlitePath,
            ..sqliteSidecars,
            ..processControlPaths,
            paths.ToolingShadowDirectory,
            ..feedSyncStatePaths,
        ];
        // Owner decision D6: the agent may read each file under the config
        // directory, with a file tool or a read-only shell program, except
        // secrets.json and the webhook route files, which hold the verification
        // secret. The keys, the database, process-control files, and the tooling
        // shadow stay read-denied. Each config file stays write-denied.
        string[] readDenyList =
        [
            paths.SecretsPath,
            paths.WebhooksDirectory,
            paths.KeysDirectory,
            sqlitePath,
            ..sqliteSidecars,
            ..processControlPaths,
            paths.ToolingShadowDirectory,
        ];
        // Shell text that names a read-denied path is denied, whatever the
        // program. A shell write to a config file meets the write list.
        string[] shellIndicatorList = readDenyList;

        return new ToolPathPolicy(
            shellEnvironment,
            writeDenyList,
            readDenyList,
            shellIndicatorList);
    }
}
