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
/// Controls when a context layer is injected into LLM calls.
/// </summary>
public enum ContextLayerTiming
{
    /// <summary>
    /// Injected on every LLM call. Use for content that changes between turns
    /// (e.g. current time).
    /// </summary>
    EveryTurn,

    /// <summary>
    /// Injected on the first LLM call and again after compaction resets context.
    /// Use for catalogs that are static for the session lifetime (e.g. tool index,
    /// skill index, subagent catalog).
    /// </summary>
    OnceAtStart
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

    /// <summary>
    /// Controls injection frequency. Defaults to <see cref="ContextLayerTiming.EveryTurn"/>
    /// for backward compatibility.
    /// </summary>
    ContextLayerTiming Timing => ContextLayerTiming.EveryTurn;
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
/// Dynamic context layer that injects the current date/time for each LLM call.
/// Content is transient and regenerated on every call so date-sensitive prompts
/// are grounded in the current runtime rather than model priors.
/// </summary>
public sealed class CurrentTimeContextLayer(TimeProvider timeProvider) : IContextLayerProvider
{
    public string GetContextLayer()
    {
        var now = timeProvider.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        return $"""
            [current-time]
            utc: {now:O}
            local: {local:yyyy-MM-dd HH:mm:ss zzz}
            day_of_week: {local:dddd}
            timezone: {TimeZoneInfo.Local.Id}
            """;
    }
}

/// <summary>
/// Context layer provider backed by a file on disk.
/// Returns empty content when the file is missing or unreadable.
/// </summary>
public sealed class FileContextLayerProvider : IContextLayerProvider
{
    private readonly string _filePath;
    private readonly ContextLayerTiming _timing;

    public FileContextLayerProvider(string filePath, ContextLayerTiming timing = ContextLayerTiming.EveryTurn)
    {
        _filePath = filePath;
        _timing = timing;
    }

    public ContextLayerTiming Timing => _timing;

    public string GetContextLayer()
    {
        try
        {
            return File.Exists(_filePath) ? File.ReadAllText(_filePath) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
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
