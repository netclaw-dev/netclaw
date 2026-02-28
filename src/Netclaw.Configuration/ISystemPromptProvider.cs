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
/// Provides a dynamic context layer that is injected into LLM calls
/// but NOT persisted as part of <c>SystemPromptSet</c>. This allows
/// transient data (e.g. tool index) to be refreshed on every call
/// without stale state in rehydrated sessions.
/// </summary>
public interface IContextLayerProvider
{
    /// <summary>
    /// Returns the context layer content, or empty string if nothing to inject.
    /// </summary>
    string GetContextLayer();
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
/// Dynamic context layer that provides the compressed tool index.
/// Updated by <see cref="ToolIndexContextLayer.Update"/> after MCP discovery completes.
/// Content is NOT persisted — rebuilt on every LLM call so rehydrated sessions
/// always see the current tool set.
/// </summary>
public sealed class ToolIndexContextLayer : IContextLayerProvider
{
    private volatile string _index = string.Empty;

    /// <summary>
    /// Replace the tool index content. Thread-safe via volatile write.
    /// </summary>
    public void Update(string index) => _index = index;

    public string GetContextLayer() => _index;
}

/// <summary>
/// Loads system prompt layers from the filesystem under <see cref="NetclawPaths.IdentityDirectory"/>.
/// Missing files are silently skipped. Falls back to legacy <c>soul/</c> paths if identity
/// files don't exist yet.
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
        // Try new identity paths first, fall back to legacy soul/ paths
        var soul = TryReadFile(_paths.SoulPath) ?? TryReadFile(_paths.PersonalityPath);
        var agents = TryReadFile(_paths.AgentsPath) ?? TryReadFile(_paths.InstructionsPath);
        var tooling = TryReadFile(_paths.ToolingPath) ?? TryReadFile(_paths.UserPreferencesPath);

        return SystemPromptAssembler.Assemble(
            soul: soul,
            agents: agents,
            tooling: tooling);
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
