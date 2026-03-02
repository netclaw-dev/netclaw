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

    // ── Other standard directories ──
    public string ProjectsDirectory => Path.Combine(BasePath, "projects");
    public string EnvironmentDirectory => Path.Combine(BasePath, "environment");
    public string SchedulesDirectory => Path.Combine(BasePath, "schedules");
    public string ConfigDirectory => Path.Combine(BasePath, "config");
    public string NetclawConfigPath => Path.Combine(ConfigDirectory, "netclaw.json");
    public string SecretsPath => Path.Combine(ConfigDirectory, "secrets.json");
    public string LogsDirectory => Path.Combine(BasePath, "logs");
    public string SessionLogsDirectory => Path.Combine(LogsDirectory, "sessions");
    public string DaemonLogPath => Path.Combine(LogsDirectory, "daemon.log");
    public string SessionsDirectory => Path.Combine(BasePath, "sessions");
    public string PidFilePath => Path.Combine(BasePath, "netclaw.pid");
    public string SqliteDbPath => Path.Combine(BasePath, "netclaw.db");
    public string McpOAuthMetadataPath => Path.Combine(ConfigDirectory, "mcp-oauth-metadata.json");

    public NetclawPaths(string? basePath = null)
    {
        BasePath = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".netclaw");
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
        Directory.CreateDirectory(ProjectsDirectory);
        Directory.CreateDirectory(EnvironmentDirectory);
        Directory.CreateDirectory(SchedulesDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SessionLogsDirectory);
        Directory.CreateDirectory(SessionsDirectory);
    }
}
