namespace Netclaw.Configuration;

/// <summary>
/// Provides the assembled system prompt for a session.
/// Injected into session actors via DI. Implementations load
/// and assemble the layered prompt content.
/// </summary>
public interface ISystemPromptProvider
{
    /// <summary>
    /// Get the assembled system prompt. Returns empty string if no layers are available.
    /// </summary>
    string GetSystemPrompt();
}

/// <summary>
/// Returns a fixed system prompt. Useful for testing.
/// </summary>
public sealed class StaticSystemPromptProvider : ISystemPromptProvider
{
    private readonly string _prompt;

    public StaticSystemPromptProvider(string prompt)
    {
        _prompt = prompt;
    }

    public string GetSystemPrompt() => _prompt;
}

/// <summary>
/// Returns an empty system prompt. Useful when no personality is configured.
/// </summary>
public sealed class NullSystemPromptProvider : ISystemPromptProvider
{
    public static readonly NullSystemPromptProvider Instance = new();

    public string GetSystemPrompt() => string.Empty;
}

/// <summary>
/// Loads system prompt layers from the filesystem under <see cref="NetclawPaths.SoulDirectory"/>.
/// Missing files are silently skipped.
/// </summary>
public sealed class FileSystemPromptProvider : ISystemPromptProvider
{
    private readonly NetclawPaths _paths;

    public FileSystemPromptProvider(NetclawPaths paths)
    {
        _paths = paths;
    }

    public string GetSystemPrompt()
    {
        return SystemPromptAssembler.Assemble(
            personality: TryReadFile(_paths.PersonalityPath),
            instructions: TryReadFile(_paths.InstructionsPath),
            userPreferences: TryReadFile(_paths.UserPreferencesPath));
    }

    /// <summary>
    /// Load a project-specific AGENTS.md overlay and re-assemble the prompt with it.
    /// </summary>
    public string GetSystemPrompt(string projectAgentsPath)
    {
        return SystemPromptAssembler.Assemble(
            personality: TryReadFile(_paths.PersonalityPath),
            instructions: TryReadFile(_paths.InstructionsPath),
            userPreferences: TryReadFile(_paths.UserPreferencesPath),
            projectAgents: TryReadFile(projectAgentsPath));
    }

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
