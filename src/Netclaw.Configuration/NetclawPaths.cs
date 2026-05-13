// -----------------------------------------------------------------------
// <copyright file="NetclawPaths.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        Directory.CreateDirectory(IdentityDirectory);
        Directory.CreateDirectory(SoulDetailDirectory);
        Directory.CreateDirectory(AgentsDetailDirectory);
        Directory.CreateDirectory(ToolingDetailDirectory);
        Directory.CreateDirectory(ToolingShadowDirectory);
        Directory.CreateDirectory(McpShadowDirectory);
        Directory.CreateDirectory(SkillsDirectory);
        Directory.CreateDirectory(SystemSkillsDirectory);
        Directory.CreateDirectory(ServerFeedsDirectory);
        Directory.CreateDirectory(ProjectsDirectory);
        Directory.CreateDirectory(ClientDirectory);
        Directory.CreateDirectory(EnvironmentDirectory);
        Directory.CreateDirectory(SchedulesDirectory);
        Directory.CreateDirectory(RemindersDirectory);
        Directory.CreateDirectory(JobsDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(WebhooksDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SessionLogsDirectory);
        Directory.CreateDirectory(AgentsDirectory);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(BinDirectory);
        Directory.CreateDirectory(KeysDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(WorkspacesDirectory);
    }
}
