// -----------------------------------------------------------------------
// <copyright file="ISystemPromptProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    /// <param name="audience">The trust audience for the current session.</param>
    /// <param name="projectDirectory">Optional project root for loading project-scoped identity files.</param>
    string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null);

    /// <summary>
    /// Get only the project-scoped identity content for a project directory.
    /// Returns null when no project instructions are available for the audience.
    /// </summary>
    string? GetProjectInstructions(TrustAudience audience, string? projectDirectory);

    /// <summary>
    /// Get the embedded AGENTS.md operating rules for the given audience.
    /// Returns null for Public audience; substituted content for Team/Personal.
    /// Used by sub-agents to inherit the core safety/operating policy layer.
    /// </summary>
    string? GetOperatingRules(TrustAudience audience);
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
/// but NOT persisted with the session. This allows transient data
/// (e.g. tool index) to be refreshed on every call without stale
/// state in rehydrated sessions.
/// </summary>
public interface IContextLayerProvider
{
    /// <summary>
    /// Returns the context layer content, or empty string if nothing to inject.
    /// </summary>
    /// <param name="audience">The trust audience for the current session turn.</param>
    string GetContextLayer(TrustAudience audience);

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

    public string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null) => _prompt;

    public string? GetProjectInstructions(TrustAudience audience, string? projectDirectory) => null;

    public string? GetOperatingRules(TrustAudience audience) => null;
}

/// <summary>
/// Returns an empty system prompt. Useful when no personality is configured.
/// </summary>
public sealed class NullSystemPromptProvider : ISystemPromptProvider
{
    public static readonly NullSystemPromptProvider Instance = new();

    public string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null) => string.Empty;

    public string? GetProjectInstructions(TrustAudience audience, string? projectDirectory) => null;

    public string? GetOperatingRules(TrustAudience audience) => null;
}

/// <summary>
/// Dynamic context layer that injects the current date/time for each LLM call.
/// Content is transient and regenerated on every call so date-sensitive prompts
/// are grounded in the current runtime rather than model priors.
/// </summary>
public sealed class CurrentTimeContextLayer(TimeProvider timeProvider) : IContextLayerProvider
{
    public string GetContextLayer(TrustAudience audience)
    {
        // All audiences need time grounding.
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

    public string GetContextLayer(TrustAudience audience)
    {
        // File-backed layers serve all audiences (e.g. tool index shadow file).
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
/// AGENTS.md is loaded from embedded resources per audience — Public gets a stripped-down version,
/// Team/Personal get the full version with placeholder substitution.
/// Missing files are silently skipped. Falls back to legacy <c>soul/</c> paths for SOUL.md if identity
/// files don't exist yet.
/// </summary>
public sealed class FileSystemPromptProvider : ISystemPromptProvider
{
    private static readonly string[] ProjectIdentityFileNames =
        [".netclaw/AGENTS.md", "CLAUDE.md", "AGENTS.md", "CONTEXT.md"];

    private const string EmbeddedAgentsResource = "Netclaw.Configuration.Resources.AGENTS.md";
    private const string EmbeddedAgentsPublicResource = "Netclaw.Configuration.Resources.AGENTS.public.md";

    private static readonly Lazy<string?> CachedAgents = new(() => ReadEmbeddedResource(EmbeddedAgentsResource));
    private static readonly Lazy<string?> CachedAgentsPublic = new(() => ReadEmbeddedResource(EmbeddedAgentsPublicResource));

    private readonly NetclawPaths _paths;

    public FileSystemPromptProvider(NetclawPaths paths)
    {
        _paths = paths;
    }

    public string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null)
    {
        // SOUL.md: always from disk, all audiences
        var soul = TryReadFile(_paths.SoulPath) ?? TryReadFile(_paths.PersonalityPath);

        // AGENTS.md: from embedded resources, audience-dependent
        var agents = audience == TrustAudience.Public
            ? CachedAgentsPublic.Value
            : SubstitutePlaceholders(CachedAgents.Value);

        // TOOLING.md and project instructions: suppressed for Public
        string? tooling = null;
        string? projectInstructions = null;
        if (audience != TrustAudience.Public)
        {
            tooling = TryReadFile(_paths.ToolingPath) ?? TryReadFile(_paths.UserPreferencesPath);
            projectInstructions = GetProjectInstructions(audience, projectDirectory);
        }

        return SystemPromptAssembler.Assemble(
            soul: soul,
            agents: agents,
            tooling: tooling,
            projectInstructions: projectInstructions);
    }

    public string? GetProjectInstructions(TrustAudience audience, string? projectDirectory)
    {
        if (audience == TrustAudience.Public)
            return null;

        return TryReadProjectIdentityFile(projectDirectory);
    }

    public string? GetOperatingRules(TrustAudience audience)
    {
        if (audience == TrustAudience.Public)
            return null;

        return SubstitutePlaceholders(CachedAgents.Value);
    }

    /// <summary>
    /// Check candidate filenames at the project root. First match wins.
    /// </summary>
    internal static string? TryReadProjectIdentityFile(string? projectDirectory)
    {
        if (string.IsNullOrEmpty(projectDirectory))
            return null;

        foreach (var candidate in ProjectIdentityFileNames)
        {
            var content = TryReadFile(Path.Combine(projectDirectory, candidate));
            if (content is not null)
                return content;
        }

        return null;
    }

    private string SubstitutePlaceholders(string? template)
    {
        if (template is null)
            return string.Empty;

        return template
            .Replace("{{SYSTEM_SKILLS_DIR}}", _paths.SystemSkillsDirectory, StringComparison.Ordinal)
            .Replace("{{IDENTITY_DIR}}", _paths.IdentityDirectory, StringComparison.Ordinal)
            .Replace("{{SOUL_PATH}}", _paths.SoulPath, StringComparison.Ordinal)
            .Replace("{{AGENTS_PATH}}", _paths.AgentsPath, StringComparison.Ordinal)
            .Replace("{{TOOLING_PATH}}", _paths.ToolingPath, StringComparison.Ordinal)
            .Replace("{{SOUL_DETAIL_DIR}}", _paths.SoulDetailDirectory, StringComparison.Ordinal)
            .Replace("{{AGENTS_DETAIL_DIR}}", _paths.AgentsDetailDirectory, StringComparison.Ordinal)
            .Replace("{{TOOLING_DETAIL_DIR}}", _paths.ToolingDetailDirectory, StringComparison.Ordinal)
            .Replace("{{SKILLS_DIR}}", _paths.SkillsDirectory, StringComparison.Ordinal)
            .Replace("{{WORKSPACES_DIR}}", _paths.WorkspacesDirectory, StringComparison.Ordinal);
    }

    private static string? ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(FileSystemPromptProvider).Assembly
            .GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
