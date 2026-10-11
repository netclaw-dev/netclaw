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
        // Owner decision (2026-10-07): the well-known credential locations below
        // the user's home are denied to agent tools, as the Netclaw secrets are.
        // The program that needs them (ssh, git, aws) reads them as the child
        // process, which no path operand names.
        var home = shellEnvironment.HomeDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Create(
            paths,
            shellEnvironment,
            skillFeeds,
            CredentialLocations(
                home,
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
                OperatingSystem.IsWindows(),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
    }

    internal static ToolPathPolicy Create(
        NetclawPaths paths,
        ShellExecutionEnvironment shellEnvironment,
        SkillFeedsConfig skillFeeds,
        string[] credentialLocations)
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
            ..credentialLocations,
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
            ..credentialLocations,
        ];
        // Shell text that names a read-denied path is denied, whatever the
        // program. A shell write to a config file meets the write list.
        string[] shellIndicatorList = readDenyList;

        return new ToolPathPolicy(
            shellEnvironment,
            writeDenyList,
            readDenyList,
            shellIndicatorList,
            credentialLocations);
    }

    // The credential locations, one table. A base folder that is not fully
    // qualified would put a relative entry in the lists, so its entries are
    // skipped. Each location is the directory or file that holds the secret:
    //   ~/.ssh ~/.aws ~/.gnupg ~/.kube ~/.azure   whole directory (keys, tokens, certificates)
    //   ~/.config/gh ~/.config/gcloud             whole directory (hosts.yml, credential databases)
    //   ~/.docker/config.json                     the file only; contexts and build state stay readable
    //   ~/.netrc ~/.git-credentials               files (and ~/_netrc on Windows)
    // The two ~/.config entries follow XDG_CONFIG_HOME as well when it differs.
    // Windows adds the AppData and ProgramData locations of the same tools and the
    // system OpenSSH folder.
    internal static string[] CredentialLocations(
        string? home,
        string? xdgConfigHome = null,
        bool windows = false,
        string? appData = null,
        string? programData = null)
    {
        var locations = new List<string>();
        if (IsFullyQualified(home))
        {
            string[] names = [".ssh", ".aws", ".gnupg", ".kube", ".azure", ".netrc", ".git-credentials"];
            locations.AddRange(names.Select(name => Path.Combine(home!, name)));
            locations.Add(Path.Combine(home!, ".docker", "config.json"));
            locations.Add(Path.Combine(home!, ".config", "gh"));
            locations.Add(Path.Combine(home!, ".config", "gcloud"));
            if (windows)
                locations.Add(Path.Combine(home!, "_netrc"));
        }

        if (IsFullyQualified(xdgConfigHome)
            && !string.Equals(
                Path.GetFullPath(xdgConfigHome!).TrimEnd(Path.DirectorySeparatorChar),
                IsFullyQualified(home) ? Path.GetFullPath(Path.Combine(home!, ".config")) : "",
                StringComparison.OrdinalIgnoreCase))
        {
            locations.Add(Path.Combine(xdgConfigHome!, "gh"));
            locations.Add(Path.Combine(xdgConfigHome!, "gcloud"));
        }

        if (windows && IsFullyQualified(appData))
        {
            locations.Add(Path.Combine(appData!, "gcloud"));
            locations.Add(Path.Combine(appData!, "GitHub CLI"));
            locations.Add(Path.Combine(appData!, "gnupg"));
        }

        if (windows && IsFullyQualified(programData))
            locations.Add(Path.Combine(programData!, "ssh"));

        return [.. locations];
    }

    private static bool IsFullyQualified(string? path)
        => !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path);
}
