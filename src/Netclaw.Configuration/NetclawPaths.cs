// -----------------------------------------------------------------------
// <copyright file="NetclawPaths.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Configuration;

/// <summary>
/// Standard directory layout for Netclaw's local file storage.
/// Encapsulates path resolution and directory creation.
/// </summary>
public sealed class NetclawPaths
{
    public string BasePath { get; }

    // ── Identity files (system prompt layers) ──
    public string IdentityDirectory => Path.Combine(BasePath, "identity");
    public string SoulPath => Path.Combine(IdentityDirectory, "SOUL.md");
    public string AgentsPath => Path.Combine(IdentityDirectory, "AGENTS.md");
    public string ToolingPath => Path.Combine(IdentityDirectory, "TOOLING.md");

    // Detail subdirectories for progressive disclosure
    public string SoulDetailDirectory => Path.Combine(IdentityDirectory, "soul");
    public string AgentsDetailDirectory => Path.Combine(IdentityDirectory, "agents");
    public string ToolingDetailDirectory => Path.Combine(IdentityDirectory, "tooling");
    public string ToolingShadowDirectory => Path.Combine(ToolingDetailDirectory, "shadow");
    public string ToolIndexShadowPath => Path.Combine(ToolingShadowDirectory, "tool-index.md");
    public string McpShadowDirectory => Path.Combine(ToolingShadowDirectory, "mcp");

    // ── Legacy paths (kept for migration detection) ──
    public string SoulDirectory => Path.Combine(BasePath, "soul");
    public string PersonalityPath => Path.Combine(SoulDirectory, "PERSONALITY.md");
    public string InstructionsPath => Path.Combine(SoulDirectory, "INSTRUCTIONS.md");
    public string UserPreferencesPath => Path.Combine(SoulDirectory, "USER.md");

    // ── Skills directory (procedural context) ──
    public string SkillsDirectory => Path.Combine(BasePath, "skills");
    public string SystemSkillsDirectory => Path.Combine(SkillsDirectory, ".system");
    public string SkillSyncStatePath => Path.Combine(SystemSkillsDirectory, ".sync-state.json");

    // ── Server feed skills (from private skill-server instances) ──
    public string ServerFeedsDirectory => Path.Combine(SkillsDirectory, ".server-feeds");

    public string ServerFeedDirectory(string feedName)
        => Path.Combine(ServerFeedsDirectory, feedName);

    public string ServerFeedSyncStatePath(string feedName)
        => Path.Combine(ServerFeedDirectory(feedName), ".sync-state.json");

    // ── Cache directory ──
    public string CacheDirectory => Path.Combine(BasePath, "cache");
    public string RestartManifestPath => Path.Combine(CacheDirectory, "restart-manifest.json");

    // ── Memory ──
    public string MemorySqliteDbPath => SqliteDbPath;

    // ── Binary directory (install location for self-contained binaries) ──
    public string BinDirectory => Path.Combine(BasePath, "bin");
    public string BinarySyncStatePath => Path.Combine(BinDirectory, ".sync-state.json");

    // ── Agent definitions directory ──
    public string AgentsDirectory => Path.Combine(BasePath, "agents");

    // ── Project workspaces ──
    /// <summary>
    /// Root directory for project workspaces. Projects are git repos with an
    /// AGENTS.md and may be nested at any depth within this directory tree.
    /// Configurable via <c>Workspaces:Directory</c> in netclaw.json; defaults to <c>{BasePath}/workspaces</c>.
    /// </summary>
    public string WorkspacesDirectory { get; }

    // ── Other standard directories ──
    public string ProjectsDirectory => Path.Combine(BasePath, "projects");
    public string ClientDirectory => Path.Combine(BasePath, "client");
    public string EnvironmentDirectory => Path.Combine(BasePath, "environment");
    public string SchedulesDirectory => Path.Combine(BasePath, "schedules");
    public string RemindersDirectory => Path.Combine(SchedulesDirectory, "reminders");
    public string JobsDirectory => Path.Combine(BasePath, "jobs");
    public string ConfigDirectory => Path.Combine(BasePath, "config");
    public string WebhooksDirectory => Path.Combine(ConfigDirectory, "webhooks");
    public string ToolApprovalsPath => Path.Combine(ConfigDirectory, "tool-approvals.json");

    /// <summary>
    /// Operator-authored hard-deny override file consulted by the
    /// structured hard-deny pipeline. Optional; missing or empty file
    /// yields zero overrides and the shipped defaults apply alone.
    /// </summary>
    public string HardDenyOverridesPath => Path.Combine(ConfigDirectory, "hard-deny-overrides.json");
    public string NetclawConfigPath => Path.Combine(ConfigDirectory, "netclaw.json");

    /// <summary>
    /// Netclaw-owned systemd <c>EnvironmentFile=</c> that supplies the installed
    /// daemon's shell-tool <c>PATH</c>. Written by <c>netclaw daemon install</c>
    /// and rehydrated by <c>netclaw doctor --fix</c> from the operator's real
    /// (captured, not guessed) <c>PATH</c>; the daemon only ever reads it. Single
    /// source of truth shared by the installer, the systemd PATH doctor check, and
    /// uninstall so the producer/consumer contract stays in lockstep.
    /// </summary>
    public string DaemonEnvironmentFilePath => Path.Combine(ConfigDirectory, "daemon.env");
    public string ClientConfigPath => Path.Combine(ClientDirectory, "config.json");
    public string SecretsPath => Path.Combine(ConfigDirectory, "secrets.json");
    public string DevicesPath => Path.Combine(ConfigDirectory, "devices.json");
    public string BootstrapStatePath => Path.Combine(ConfigDirectory, "bootstrap-state.json");
    public string LogsDirectory => Path.Combine(BasePath, "logs");
    /// <summary>
    /// Per-session log files live at <c>{SessionLogsDirectory}/{sanitized_id}/session.log</c>.
    /// This tree is deliberately kept outside <see cref="SessionsDirectory"/> so
    /// the agent's file_read tool (scoped to <c>{session_dir}</c>) cannot observe
    /// its own audit trail.
    /// </summary>
    public string SessionLogsDirectory => Path.Combine(LogsDirectory, "sessions");
    public string DaemonLogPath => Path.Combine(LogsDirectory, "daemon.log");
    public string SessionsDirectory => Path.Combine(BasePath, "sessions");
    public string PidFilePath => Path.Combine(BasePath, "netclaw.pid");
    public string LockFilePath => Path.Combine(BasePath, "netclaw.lock");
    public string SqliteDbPath => Path.Combine(BasePath, "netclaw.db");
    public string McpOAuthMetadataPath => Path.Combine(ConfigDirectory, "mcp-oauth-metadata.json");
    public string KeysDirectory => Path.Combine(BasePath, "keys");

    public NetclawPaths(string? basePath = null, string? workspacesDirectory = null)
    {
        BasePath = PathExpansion.ExpandHome(basePath)
            ?? PathExpansion.ExpandHome(Environment.GetEnvironmentVariable("NETCLAW_HOME"))
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".netclaw");
        WorkspacesDirectory = PathExpansion.ExpandHome(workspacesDirectory) ?? Path.Combine(BasePath, "workspaces");
    }

    /// <summary>
    /// Create all standard subdirectories if they don't exist.
    /// Existing files and directories are preserved.
    /// </summary>
    public void EnsureDirectoriesExist()
    {
        var failures = new List<NetclawDirectoryInitializationFailure>();

        foreach (var directory in StandardDirectories())
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                failures.Add(new NetclawDirectoryInitializationFailure(directory, ex));
            }
        }

        if (failures.Count > 0)
            throw new NetclawDirectoryInitializationException(BasePath, failures);
    }

    private IEnumerable<string> StandardDirectories()
    {
        yield return IdentityDirectory;
        yield return SoulDetailDirectory;
        yield return AgentsDetailDirectory;
        yield return ToolingDetailDirectory;
        yield return ToolingShadowDirectory;
        yield return McpShadowDirectory;
        yield return SkillsDirectory;
        yield return SystemSkillsDirectory;
        yield return ServerFeedsDirectory;
        yield return ProjectsDirectory;
        yield return ClientDirectory;
        yield return EnvironmentDirectory;
        yield return SchedulesDirectory;
        yield return RemindersDirectory;
        yield return JobsDirectory;
        yield return ConfigDirectory;
        yield return WebhooksDirectory;
        yield return LogsDirectory;
        yield return SessionLogsDirectory;
        yield return AgentsDirectory;
        yield return SessionsDirectory;
        yield return BinDirectory;
        yield return KeysDirectory;
        yield return CacheDirectory;
        yield return WorkspacesDirectory;
    }
}

public sealed class NetclawDirectoryInitializationException : IOException
{
    public NetclawDirectoryInitializationException(
        string basePath,
        IReadOnlyList<NetclawDirectoryInitializationFailure> failures)
        : base(BuildMessage(basePath, failures), new AggregateException(failures.Select(f => f.Exception)))
    {
        BasePath = basePath;
        Failures = failures.ToArray();
    }

    public string BasePath { get; }

    public IReadOnlyList<NetclawDirectoryInitializationFailure> Failures { get; }

    private static string BuildMessage(string basePath, IReadOnlyList<NetclawDirectoryInitializationFailure> failures)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Failed to initialize Netclaw directories under '{basePath}'.");
        builder.AppendLine();
        builder.AppendLine("The following directories could not be created:");
        foreach (var failure in failures)
            builder.AppendLine($"- {failure.DirectoryPath}: {failure.Exception.Message}");

        builder.AppendLine();
        builder.AppendLine($"Process user: {DescribeProcessUser()}");
        builder.AppendLine();
        builder.AppendLine("If this is a Docker bind mount, the host directory is probably not writable by the container's netclaw user.");
        builder.AppendLine("The official Docker entrypoint should repair writable bind mounts automatically. If it was bypassed, pre-create the host path with:");
        builder.AppendLine("  sudo chown -R 1654:1654 <host-netclaw-data-directory>");
        builder.AppendLine("For non-Docker installs, grant the current user write access to the Netclaw base directory.");
        return builder.ToString().TrimEnd();
    }

    private static string DescribeProcessUser()
    {
        var userName = Environment.UserName;
        if (!OperatingSystem.IsLinux())
            return userName;

        var ids = TryReadLinuxProcessIds();
        return ids is null
            ? userName
            : $"{userName} (uid={ids.Value.Uid}, gid={ids.Value.Gid})";
    }

    private static (string Uid, string Gid)? TryReadLinuxProcessIds()
    {
        try
        {
            string? uid = null;
            string? gid = null;
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    uid = line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];
                else if (line.StartsWith("Gid:", StringComparison.Ordinal))
                    gid = line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];

                if (uid is not null && gid is not null)
                    return (uid, gid);
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }
}

public sealed record NetclawDirectoryInitializationFailure(string DirectoryPath, Exception Exception);
