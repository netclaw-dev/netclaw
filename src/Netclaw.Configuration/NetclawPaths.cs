namespace Netclaw.Configuration;

/// <summary>
/// Standard directory layout for Netclaw's local file storage.
/// Encapsulates path resolution and directory creation.
/// </summary>
public sealed class NetclawPaths
{
    public string BasePath { get; }

    public string SoulDirectory => Path.Combine(BasePath, "soul");
    public string PersonalityPath => Path.Combine(SoulDirectory, "PERSONALITY.md");
    public string InstructionsPath => Path.Combine(SoulDirectory, "INSTRUCTIONS.md");
    public string UserPreferencesPath => Path.Combine(SoulDirectory, "USER.md");

    public string ProjectsDirectory => Path.Combine(BasePath, "projects");
    public string EnvironmentDirectory => Path.Combine(BasePath, "environment");
    public string SchedulesDirectory => Path.Combine(BasePath, "schedules");
    public string ConfigDirectory => Path.Combine(BasePath, "config");
    public string NetclawConfigPath => Path.Combine(ConfigDirectory, "netclaw.json");
    public string SecretsPath => Path.Combine(ConfigDirectory, "secrets.json");
    public string LogsDirectory => Path.Combine(BasePath, "logs");

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
        Directory.CreateDirectory(SoulDirectory);
        Directory.CreateDirectory(ProjectsDirectory);
        Directory.CreateDirectory(EnvironmentDirectory);
        Directory.CreateDirectory(SchedulesDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
